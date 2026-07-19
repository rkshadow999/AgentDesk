using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentDesk.Engine.Acp;

namespace AgentDesk.Engine.Tests;

internal sealed class AcpTranscriptReplay : IAsyncDisposable
{
    private const string FixtureFormat = "agentdesk.acp-transcript";
    private const int SupportedSchemaVersion = 1;
    private const int MaximumFixtureBytes = 1024 * 1024;
    private const int MaximumRecordCount = 256;
    private const int MaximumScannedStringLength = 64 * 1024;
    private static readonly TimeSpan SensitivePatternTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex[] SensitiveValuePatterns =
    [
        SensitivePattern(@"sk-[A-Za-z0-9_-]{16,}"),
        SensitivePattern(@"(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}"),
        SensitivePattern(@"github_pat_[A-Za-z0-9_]{20,}"),
        SensitivePattern(@"glpat-[A-Za-z0-9_-]{20,}"),
        SensitivePattern(@"xox[baprs]-[A-Za-z0-9-]{10,}"),
        SensitivePattern(@"npm_[A-Za-z0-9]{20,}"),
        SensitivePattern(@"AKIA[0-9A-Z]{16}"),
        SensitivePattern(@"AIza[0-9A-Za-z_-]{30,}"),
        SensitivePattern(@"Bearer\s+[A-Za-z0-9._~+/=-]{12,}"),
    ];

    private readonly Pipe _engineToClient = new();
    private readonly Pipe _clientToEngine = new();
    private readonly IReadOnlyList<TranscriptEntry> _entries;
    private readonly StreamReader _clientReader;
    private int _nextEntry;
    private bool _clientDisposed;
    private bool _disposed;

    private AcpTranscriptReplay(IReadOnlyList<TranscriptEntry> entries)
    {
        _entries = entries;
        Client = new AcpEngineClient(
            _engineToClient.Reader.AsStream(),
            _clientToEngine.Writer.AsStream());
        _clientReader = new StreamReader(
            _clientToEngine.Reader.AsStream(),
            Encoding.UTF8,
            leaveOpen: true);
    }

    public AcpEngineClient Client { get; }

    public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(5));

    public static async Task<AcpTranscriptReplay> LoadAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await LoadAsync(stream, path).ConfigureAwait(false);
    }

    internal static async Task<AcpTranscriptReplay> LoadAsync(Stream stream, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (stream.CanSeek && stream.Length - stream.Position > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"ACP transcript '{sourceName}' exceeded the maximum of {MaximumFixtureBytes} bytes.");
        }

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var headerLine = await reader.ReadLineAsync().ConfigureAwait(false);
        if (headerLine is null)
        {
            throw new InvalidDataException($"ACP transcript '{sourceName}' is empty.");
        }
        var bytesRead = CountLineBytes(headerLine);
        EnsureFixtureWithinLimit(bytesRead, sourceName);

        using var header = JsonDocument.Parse(headerLine);
        var root = header.RootElement;
        if (!root.TryGetProperty("fixtureFormat", out var format) ||
            !string.Equals(format.GetString(), FixtureFormat, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"ACP transcript '{sourceName}' has an unsupported fixture format.");
        }
        if (!root.TryGetProperty("schemaVersion", out var versionElement) ||
            !versionElement.TryGetInt32(out var schemaVersion))
        {
            throw new InvalidDataException(
                $"ACP transcript '{sourceName}' does not declare a schema version.");
        }
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new NotSupportedException(
                $"ACP transcript schema version {schemaVersion} is not supported. " +
                $"Expected version {SupportedSchemaVersion}.");
        }
        ValidateRedactionDeclaration(root, sourceName);
        ScanForSensitiveValues(root, sourceName, lineNumber: 1);

        var entries = new List<TranscriptEntry>();
        var lineNumber = 1;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            bytesRead += CountLineBytes(line);
            EnsureFixtureWithinLimit(bytesRead, sourceName);
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidDataException(
                    $"ACP transcript '{sourceName}' contains an empty record at line {lineNumber}.");
            }

            using var document = JsonDocument.Parse(line);
            var entry = document.RootElement;
            ScanForSensitiveValues(entry, sourceName, lineNumber);
            if (entries.Count >= MaximumRecordCount)
            {
                throw new InvalidDataException(
                    $"ACP transcript '{sourceName}' exceeded the maximum record count.");
            }
            var sequence = ReadRequiredInt32(entry, "sequence", sourceName, lineNumber);
            if (sequence != entries.Count + 1)
            {
                throw new InvalidDataException(
                    $"ACP transcript '{sourceName}' has an out-of-order record at line {lineNumber}.");
            }

            var direction = ReadRequiredString(entry, "direction", sourceName, lineNumber);
            JsonElement? message = null;
            string? lifecycleEvent = null;
            switch (direction)
            {
                case "client_to_engine":
                case "engine_to_client":
                    if (!entry.TryGetProperty("message", out var messageElement) ||
                        messageElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException(
                            $"ACP transcript '{sourceName}' has an invalid message at line {lineNumber}.");
                    }
                    message = messageElement.Clone();
                    break;
                case "lifecycle":
                    lifecycleEvent = ReadRequiredString(
                        entry,
                        "event",
                        sourceName,
                        lineNumber);
                    break;
                default:
                    throw new InvalidDataException(
                        $"ACP transcript '{sourceName}' has an unsupported direction at line {lineNumber}.");
            }

            entries.Add(new TranscriptEntry(sequence, direction, message, lifecycleEvent));
        }

        return new AcpTranscriptReplay(entries);
    }

    private static void ValidateRedactionDeclaration(JsonElement header, string sourceName)
    {
        if (!header.TryGetProperty("redaction", out var redaction) ||
            redaction.ValueKind != JsonValueKind.Object ||
            !redaction.TryGetProperty("strategy", out var strategy) ||
            strategy.ValueKind != JsonValueKind.String ||
            !string.Equals(strategy.GetString(), "synthetic", StringComparison.Ordinal) ||
            !IsDeclaredFalse(redaction, "containsPromptText") ||
            !IsDeclaredFalse(redaction, "containsCredentials") ||
            !IsDeclaredFalse(redaction, "containsRealPaths"))
        {
            throw new InvalidDataException(
                $"ACP transcript '{sourceName}' has an unsafe redaction declaration.");
        }
    }

    private static bool IsDeclaredFalse(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.False;

    private static void ScanForSensitiveValues(
        JsonElement element,
        string sourceName,
        int lineNumber)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ScanForSensitiveValues(property.Value, sourceName, lineNumber);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ScanForSensitiveValues(item, sourceName, lineNumber);
                }
                break;
            case JsonValueKind.String:
                ScanStringValue(element.GetString()!, sourceName, lineNumber);
                break;
        }
    }

    private static void ScanStringValue(string value, string sourceName, int lineNumber)
    {
        if (value.Length > MaximumScannedStringLength)
        {
            throw SensitiveValueException(sourceName, lineNumber);
        }
        if (IsWindowsAbsolutePath(value) || value.StartsWith('/'))
        {
            throw SensitiveValueException(sourceName, lineNumber);
        }
        foreach (var pattern in SensitiveValuePatterns)
        {
            if (pattern.IsMatch(value))
            {
                throw SensitiveValueException(sourceName, lineNumber);
            }
        }
    }

    private static bool IsWindowsAbsolutePath(string value) =>
        value.Length >= 3 &&
        char.IsAsciiLetter(value[0]) &&
        value[1] == ':' &&
        value[2] is '\\' or '/' ||
        value.StartsWith("\\\\", StringComparison.Ordinal);

    private static InvalidDataException SensitiveValueException(
        string sourceName,
        int lineNumber) =>
        new($"ACP transcript '{sourceName}' contains a sensitive value at line {lineNumber}.");

    private static Regex SensitivePattern(string pattern) =>
        new(
            pattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            SensitivePatternTimeout);

    private static int CountLineBytes(string line)
    {
        try
        {
            return checked(Encoding.UTF8.GetByteCount(line) + 1);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("The ACP transcript line was too large.", exception);
        }
    }

    private static void EnsureFixtureWithinLimit(long bytesRead, string sourceName)
    {
        if (bytesRead > MaximumFixtureBytes)
        {
            throw new InvalidDataException(
                $"ACP transcript '{sourceName}' exceeded the maximum of {MaximumFixtureBytes} bytes.");
        }
    }

    public async Task<JsonDocument> ExpectClientMessageAsync(string? expectedMethod = null)
    {
        var entry = NextEntry("client_to_engine");
        var line = await _clientReader.ReadLineAsync(Timeout.Token).ConfigureAwait(false);
        if (line is null)
        {
            throw new EndOfStreamException(
                $"The ACP client closed before transcript record {entry.Sequence}.");
        }

        var actual = JsonDocument.Parse(line);
        if (!JsonElement.DeepEquals(entry.Message!.Value, actual.RootElement))
        {
            actual.Dispose();
            throw new InvalidDataException(
                $"ACP client message did not match transcript record {entry.Sequence}.");
        }
        if (expectedMethod is not null &&
            (!actual.RootElement.TryGetProperty("method", out var method) ||
             !string.Equals(method.GetString(), expectedMethod, StringComparison.Ordinal)))
        {
            actual.Dispose();
            throw new InvalidDataException(
                $"ACP transcript record {entry.Sequence} did not contain method '{expectedMethod}'.");
        }
        return actual;
    }

    public async Task SendEngineMessageAsync()
    {
        var entry = NextEntry("engine_to_client");
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry.Message!.Value);
        await _engineToClient.Writer.WriteAsync(payload, Timeout.Token).ConfigureAwait(false);
        await _engineToClient.Writer.WriteAsync("\n"u8.ToArray(), Timeout.Token)
            .ConfigureAwait(false);
    }

    public async Task ShutdownAsync()
    {
        var entry = NextEntry("lifecycle");
        if (!string.Equals(entry.LifecycleEvent, "shutdown", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"ACP transcript record {entry.Sequence} is not a shutdown event.");
        }

        await DisposeClientAsync().ConfigureAwait(false);
        var trailingMessage = await _clientReader.ReadLineAsync(Timeout.Token).ConfigureAwait(false);
        if (trailingMessage is not null)
        {
            throw new InvalidDataException(
                "The ACP client wrote an unexpected message while shutting down.");
        }
    }

    public void AssertComplete()
    {
        if (_nextEntry != _entries.Count)
        {
            throw new InvalidDataException(
                $"ACP transcript stopped at record {_nextEntry}; {_entries.Count - _nextEntry} remain.");
        }
    }

    private TranscriptEntry NextEntry(string expectedDirection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_nextEntry >= _entries.Count)
        {
            throw new InvalidDataException(
                $"ACP transcript ended before a '{expectedDirection}' record was available.");
        }

        var entry = _entries[_nextEntry];
        if (!string.Equals(entry.Direction, expectedDirection, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"ACP transcript record {entry.Sequence} has direction '{entry.Direction}', " +
                $"not '{expectedDirection}'.");
        }
        _nextEntry++;
        return entry;
    }

    private async Task DisposeClientAsync()
    {
        if (_clientDisposed)
        {
            return;
        }
        _clientDisposed = true;
        await Client.DisposeAsync().ConfigureAwait(false);
    }

    private static int ReadRequiredInt32(
        JsonElement element,
        string propertyName,
        string sourceName,
        int lineNumber)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"ACP transcript '{sourceName}' has an invalid '{propertyName}' at line {lineNumber}.");
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName,
        string sourceName,
        int lineNumber)
    {
        if (element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 } value)
        {
            return value;
        }
        throw new InvalidDataException(
            $"ACP transcript '{sourceName}' has an invalid '{propertyName}' at line {lineNumber}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Timeout.Cancel();
        await DisposeClientAsync().ConfigureAwait(false);
        _clientReader.Dispose();
        await _engineToClient.Writer.CompleteAsync().ConfigureAwait(false);
        await _clientToEngine.Reader.CompleteAsync().ConfigureAwait(false);
        Timeout.Dispose();
    }

    private sealed record TranscriptEntry(
        int Sequence,
        string Direction,
        JsonElement? Message,
        string? LifecycleEvent);
}
