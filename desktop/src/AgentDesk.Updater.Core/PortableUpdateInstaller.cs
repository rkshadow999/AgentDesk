using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDesk.Updater.Core;

public sealed record PortableInstallRequest(
    string InstallationDirectory,
    string StateDirectory,
    string StagedPayloadDirectory,
    SemanticVersion Version,
    string EntryPoint,
    int? ParentProcessId,
    TimeSpan ParentExitTimeout,
    IReadOnlyList<string> RestartArguments);

public sealed record PortableInstallResult(
    SemanticVersion Version,
    string InstallationDirectory,
    string BackupDirectory);

internal interface IUpdateProcessController
{
    Task WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken);

    void Start(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments);
}

internal interface IUpdateDirectoryOperations
{
    bool Exists(string path);

    void Create(string path);

    void Move(string source, string destination);
}

public sealed class PortableUpdateInstaller
{
    private const int JournalSchemaVersion = 1;
    private readonly IUpdateDirectoryOperations _directories;
    private readonly IUpdateProcessController _processes;

    public PortableUpdateInstaller()
        : this(new SystemUpdateDirectoryOperations(), new SystemUpdateProcessController())
    {
    }

    internal PortableUpdateInstaller(
        IUpdateDirectoryOperations directories,
        IUpdateProcessController processes)
    {
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
    }

    public async Task<PortableInstallResult> InstallAsync(
        PortableInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var paths = ValidateRequest(request);
        _directories.Create(paths.StateDirectory);
        await using var updateLock = AcquireUpdateLock(paths.StateDirectory);

        if (request.ParentProcessId is { } processId)
        {
            await _processes.WaitForExitAsync(
                processId,
                request.ParentExitTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        await RecoverCoreAsync(
            paths.InstallationDirectory,
            paths.StateDirectory,
            cancellationToken).ConfigureAwait(false);

        var transactionId = Guid.NewGuid().ToString("N");
        var backupsRoot = Path.Combine(paths.StateDirectory, "backups");
        var failedRoot = Path.Combine(paths.StateDirectory, "failed");
        _directories.Create(backupsRoot);
        _directories.Create(failedRoot);
        var backupDirectory = Path.Combine(backupsRoot, transactionId);
        var failedDirectory = Path.Combine(failedRoot, transactionId);
        var journal = new TransactionJournal(
            JournalSchemaVersion,
            transactionId,
            paths.InstallationDirectory,
            paths.StagedPayloadDirectory,
            backupDirectory,
            request.EntryPoint,
            TransactionPhase.Prepared);
        var journalPath = JournalPath(paths.StateDirectory);

        await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
        try
        {
            _directories.Move(paths.InstallationDirectory, backupDirectory);
            journal = journal with { Phase = TransactionPhase.BackupCreated };
            await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

            _directories.Move(paths.StagedPayloadDirectory, paths.InstallationDirectory);
            journal = journal with { Phase = TransactionPhase.Activated };
            await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);

            var executable = ResolveEntryPoint(paths.InstallationDirectory, request.EntryPoint);
            _processes.Start(executable, paths.InstallationDirectory, request.RestartArguments);
            journal = journal with { Phase = TransactionPhase.Completed };
            await WriteJournalAsync(journalPath, journal, cancellationToken).ConfigureAwait(false);
            File.Delete(journalPath);
            return new PortableInstallResult(
                request.Version,
                paths.InstallationDirectory,
                backupDirectory);
        }
        catch
        {
            if (_directories.Exists(backupDirectory))
            {
                try
                {
                    if (_directories.Exists(paths.InstallationDirectory))
                    {
                        _directories.Move(paths.InstallationDirectory, failedDirectory);
                    }

                    _directories.Move(backupDirectory, paths.InstallationDirectory);
                    File.Delete(journalPath);
                }
                catch
                {
                    // Preserve the journal and original exception. A later recovery run can retry.
                }
            }
            else
            {
                File.Delete(journalPath);
            }

            throw;
        }
    }

    public async Task<bool> RecoverAsync(
        string installationDirectory,
        string stateDirectory,
        CancellationToken cancellationToken = default)
    {
        var installation = UpdatePathSafety.FullPath(installationDirectory);
        var state = UpdatePathSafety.FullPath(stateDirectory);
        _directories.Create(state);
        UpdatePathSafety.EnsureNoReparsePoints(state);
        await using var updateLock = AcquireUpdateLock(state);
        return await RecoverCoreAsync(installation, state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RecoverCoreAsync(
        string installationDirectory,
        string stateDirectory,
        CancellationToken cancellationToken)
    {
        var journalPath = JournalPath(stateDirectory);
        if (!File.Exists(journalPath))
        {
            return false;
        }

        var journal = await ReadJournalAsync(journalPath, cancellationToken).ConfigureAwait(false);
        ValidateJournal(journal, installationDirectory, stateDirectory);
        if (journal.Phase == TransactionPhase.Completed)
        {
            File.Delete(journalPath);
            return true;
        }

        if (!_directories.Exists(journal.BackupDirectory))
        {
            if (journal.Phase == TransactionPhase.Prepared &&
                _directories.Exists(installationDirectory))
            {
                File.Delete(journalPath);
                return true;
            }

            throw new UpdateSecurityException("The interrupted update cannot be recovered automatically.");
        }

        if (_directories.Exists(installationDirectory))
        {
            var failedRoot = Path.Combine(stateDirectory, "failed");
            _directories.Create(failedRoot);
            var failedDirectory = Path.Combine(failedRoot, $"recovered-{journal.TransactionId}");
            if (_directories.Exists(failedDirectory))
            {
                throw new UpdateSecurityException("The recovery quarantine directory already exists.");
            }

            _directories.Move(installationDirectory, failedDirectory);
        }

        _directories.Move(journal.BackupDirectory, installationDirectory);
        File.Delete(journalPath);
        return true;
    }

    private static ValidatedPaths ValidateRequest(PortableInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.RestartArguments);
        var installation = UpdatePathSafety.FullPath(request.InstallationDirectory);
        var state = UpdatePathSafety.FullPath(request.StateDirectory);
        var staged = UpdatePathSafety.FullPath(request.StagedPayloadDirectory);
        if (!Directory.Exists(installation) ||
            !Directory.Exists(staged) ||
            !UpdateManifestVerifier.IsSafeEntryPoint(request.EntryPoint) ||
            request.ParentExitTimeout <= TimeSpan.Zero ||
            request.ParentExitTimeout > TimeSpan.FromHours(24) ||
            request.ParentProcessId is <= 0 ||
            request.ParentProcessId == Environment.ProcessId ||
            !string.Equals(Path.GetPathRoot(installation), Path.GetPathRoot(state), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(installation, state, StringComparison.OrdinalIgnoreCase) ||
            UpdatePathSafety.IsContained(installation, state) ||
            UpdatePathSafety.IsContained(state, installation))
        {
            throw new UpdateSecurityException("The portable update installation request is unsafe.");
        }

        var stagingRoot = Path.Combine(state, "staging");
        UpdatePathSafety.EnsureContained(stagingRoot, staged, "staged payload");
        UpdatePathSafety.EnsureNoReparsePoints(installation);
        UpdatePathSafety.EnsureNoReparsePoints(state);
        UpdatePathSafety.EnsureNoReparsePoints(staged);
        _ = ResolveEntryPoint(staged, request.EntryPoint);
        ValidateRestartArguments(request.RestartArguments);
        return new ValidatedPaths(installation, state, staged);
    }

    private static void ValidateRestartArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 128 || arguments.Any(argument =>
                argument is null ||
                argument.Length > 8192 ||
                argument.Any(char.IsControl)))
        {
            throw new UpdateSecurityException("The restart argument list is unsafe.");
        }
    }

    private static string ResolveEntryPoint(string root, string relativeEntryPoint)
    {
        var executable = Path.Combine(
            root,
            relativeEntryPoint.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        UpdatePathSafety.EnsureContained(root, executable, "update entry point");
        if (!File.Exists(executable) ||
            (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UpdateSecurityException("The portable update entry point is missing or unsafe.");
        }

        return executable;
    }

    private static async Task<TransactionJournal> ReadJournalAsync(
        string journalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(journalPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > 16 * 1024)
            {
                throw new UpdateSecurityException("The update transaction journal is invalid.");
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var properties = document.RootElement.EnumerateObject().ToArray();
            var expected = new HashSet<string>(
                [
                    "schemaVersion", "transactionId", "installationDirectory",
                    "stagedPayloadDirectory", "backupDirectory", "entryPoint", "phase",
                ],
                StringComparer.Ordinal);
            if (properties.Length != expected.Count ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != expected.Count ||
                properties.Any(property => !expected.Contains(property.Name)))
            {
                throw new UpdateSecurityException("The update transaction journal is invalid.");
            }

            return JsonSerializer.Deserialize<TransactionJournal>(bytes)
                ?? throw new UpdateSecurityException("The update transaction journal is invalid.");
        }
        catch (UpdateSecurityException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or InvalidOperationException)
        {
            throw new UpdateSecurityException("The update transaction journal is invalid.", exception);
        }
    }

    private static void ValidateJournal(
        TransactionJournal journal,
        string installationDirectory,
        string stateDirectory)
    {
        if (journal.SchemaVersion != JournalSchemaVersion ||
            journal.TransactionId.Length != 32 ||
            !journal.TransactionId.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            !Enum.IsDefined(journal.Phase) ||
            !UpdateManifestVerifier.IsSafeEntryPoint(journal.EntryPoint) ||
            !string.Equals(
                UpdatePathSafety.FullPath(journal.InstallationDirectory),
                installationDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateSecurityException("The update transaction journal is invalid.");
        }

        UpdatePathSafety.EnsureContained(
            Path.Combine(stateDirectory, "staging"),
            journal.StagedPayloadDirectory,
            "journal staging path");
        UpdatePathSafety.EnsureContained(
            Path.Combine(stateDirectory, "backups"),
            journal.BackupDirectory,
            "journal backup path");
    }

    private static async Task WriteJournalAsync(
        string journalPath,
        TransactionJournal journal,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{journalPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, journalPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static FileStream AcquireUpdateLock(string stateDirectory) => new(
        Path.Combine(stateDirectory, "update.lock"),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 1,
        FileOptions.Asynchronous | FileOptions.WriteThrough);

    private static string JournalPath(string stateDirectory) =>
        Path.Combine(stateDirectory, "transaction.json");

    private sealed record ValidatedPaths(
        string InstallationDirectory,
        string StateDirectory,
        string StagedPayloadDirectory);

    private enum TransactionPhase
    {
        [JsonStringEnumMemberName("prepared")]
        Prepared,
        [JsonStringEnumMemberName("backupCreated")]
        BackupCreated,
        [JsonStringEnumMemberName("activated")]
        Activated,
        [JsonStringEnumMemberName("completed")]
        Completed,
    }

    private sealed record TransactionJournal(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("transactionId")] string TransactionId,
        [property: JsonPropertyName("installationDirectory")] string InstallationDirectory,
        [property: JsonPropertyName("stagedPayloadDirectory")] string StagedPayloadDirectory,
        [property: JsonPropertyName("backupDirectory")] string BackupDirectory,
        [property: JsonPropertyName("entryPoint")] string EntryPoint,
        [property: JsonPropertyName("phase"), JsonConverter(typeof(JsonStringEnumConverter))]
        TransactionPhase Phase);

    private sealed class SystemUpdateDirectoryOperations : IUpdateDirectoryOperations
    {
        public bool Exists(string path) => Directory.Exists(path);

        public void Create(string path) => Directory.CreateDirectory(path);

        public void Move(string source, string destination) => Directory.Move(source, destination);
    }

    private sealed class SystemUpdateProcessController : IUpdateProcessController
    {
        public async Task WaitForExitAsync(
            int processId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return;
            }

            using (process)
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeoutSource.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("The AgentDesk process did not exit before the update timeout.");
                }
            }
        }

        public void Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("AgentDesk could not be restarted after the update.");
        }
    }
}
