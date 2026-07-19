using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentDesk.Cloud.Client;

public sealed class SessionSyncDocument
{
    public const int MaximumDocumentBytes = 64 * 1024 * 1024 - 16;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly byte[] _utf8Json;

    private SessionSyncDocument(byte[] utf8Json)
    {
        _utf8Json = utf8Json;
    }

    public int ByteLength => _utf8Json.Length;

    public static SessionSyncDocument FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        byte[] bytes;
        try
        {
            var byteCount = StrictUtf8.GetByteCount(json);
            if (byteCount > MaximumDocumentBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(json),
                    "The sync document exceeds the absolute size limit.");
            }

            bytes = StrictUtf8.GetBytes(json);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The sync document is not valid UTF-8 JSON.", nameof(json), exception);
        }

        try
        {
            return FromOwnedUtf8Json(bytes, nameof(json));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public byte[] ExportUtf8Json() => _utf8Json.ToArray();

    public override string ToString() =>
        $"SessionSyncDocument {{ ByteLength = {_utf8Json.Length} }}";

    internal static SessionSyncDocument FromUtf8Json(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaximumDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(utf8Json),
                "The sync document exceeds the absolute size limit.");
        }

        var copy = utf8Json.ToArray();
        try
        {
            return FromOwnedUtf8Json(copy, nameof(utf8Json));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(copy);
            throw;
        }
    }

    private static SessionSyncDocument FromOwnedUtf8Json(byte[] bytes, string parameterName)
    {
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    "A sync document must contain a JSON object.",
                    parameterName);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The sync document is not valid UTF-8 JSON.",
                parameterName,
                exception);
        }

        return new SessionSyncDocument(bytes);
    }
}
