using AgentDesk.App.Bridge;
using AgentDesk.Core.Engine;
using AgentDesk.Platform.Windows.Backup;

namespace AgentDesk.App.Maintenance;

public enum AgentDeskPackageMode
{
    Portable,
    Msix,
}

public sealed record AgentDeskMaintenanceOptions
{
    public AgentDeskMaintenanceOptions(
        string DataDirectory,
        AgentDeskPackageMode PackageMode,
        string CurrentVersion,
        int ParentProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(CurrentVersion);
        if (!Enum.IsDefined(PackageMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PackageMode));
        }
        if (!AgentDesk.Updater.Core.SemanticVersion.TryParse(CurrentVersion, out _))
        {
            throw new ArgumentException("The installed version is invalid.", nameof(CurrentVersion));
        }
        if (ParentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ParentProcessId));
        }

        this.DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory));
        this.PackageMode = PackageMode;
        this.CurrentVersion = CurrentVersion;
        this.ParentProcessId = ParentProcessId;
    }

    public string DataDirectory { get; }

    public AgentDeskPackageMode PackageMode { get; }

    public string CurrentVersion { get; }

    public int ParentProcessId { get; }
}

public interface IAgentDeskMaintenanceHost
{
    Task<IAgentDeskMaintenanceLease> BeginMaintenanceAsync(
        CancellationToken cancellationToken = default);
}

public interface IAgentDeskMaintenanceLease : IAsyncDisposable
{
    string WorkspacePath { get; }

    Task<EngineSessionDocument> ExportSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<SessionId> ImportSessionAsync(
        EngineSessionDocument document,
        CancellationToken cancellationToken = default);

    Task StopEngineAsync(CancellationToken cancellationToken = default);
}

public sealed class AgentDeskMaintenanceCoordinator : IDisposable
{
    private readonly IAgentDeskMaintenanceHost _host;
    private readonly SessionDocumentFileStore _documents;
    private readonly AgentDeskBackupService _backups;
    private readonly AgentDeskUpdateCoordinator _updates;
    private readonly AgentDeskMaintenanceOptions _options;
    private readonly Func<WebEvent, Task> _publish;
    private readonly Func<CancellationToken, Task> _prepareRestore;
    private readonly Func<CancellationToken, Task> _restart;
    private readonly Func<CancellationToken, Task> _exit;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _requestSync = new();
    private readonly HashSet<string> _usedRequestIds = new(StringComparer.Ordinal);
    private bool _disposed;

    public AgentDeskMaintenanceCoordinator(
        IAgentDeskMaintenanceHost host,
        SessionDocumentFileStore documents,
        AgentDeskBackupService backups,
        AgentDeskUpdateCoordinator updates,
        AgentDeskMaintenanceOptions options,
        Func<WebEvent, Task> publish,
        Func<CancellationToken, Task> prepareRestore,
        Func<CancellationToken, Task> restart,
        Func<CancellationToken, Task> exit)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _prepareRestore = prepareRestore ?? throw new ArgumentNullException(nameof(prepareRestore));
        _restart = restart ?? throw new ArgumentNullException(nameof(restart));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
    }

    public async Task HandleAsync(
        MaintenanceWebCommand command,
        string? nativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operation = OperationName(command);

        if (RequiresFile(command) && string.IsNullOrWhiteSpace(nativePath))
        {
            await _publish(new MaintenanceCancelledWebEvent(command.RequestId, operation))
                .ConfigureAwait(false);
            return;
        }

        bool duplicate;
        lock (_requestSync)
        {
            duplicate = !_usedRequestIds.Add(command.RequestId);
        }
        if (duplicate)
        {
            await _publish(new MaintenanceErrorWebEvent(command.RequestId, operation))
                .ConfigureAwait(false);
            return;
        }

        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await _publish(new MaintenanceErrorWebEvent(command.RequestId, operation))
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await using var lease = await _host
                .BeginMaintenanceAsync(cancellationToken)
                .ConfigureAwait(false);
            switch (command)
            {
                case SessionExportWebCommand export:
                    await ExportSessionAsync(export, nativePath!, lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case SessionImportWebCommand import:
                    await ImportSessionAsync(import, nativePath!, lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case BackupCreateWebCommand backup:
                    await CreateBackupAsync(backup, nativePath!, lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case BackupRestoreWebCommand restore:
                    await RestoreBackupAsync(restore, nativePath!, lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case UpdateCheckWebCommand update:
                    await CheckUpdateAsync(update, cancellationToken).ConfigureAwait(false);
                    break;
                case UpdateApplyWebCommand update:
                    await ApplyUpdateAsync(update, lease, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await _publish(new MaintenanceErrorWebEvent(command.RequestId, operation))
                .ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _operationGate.Dispose();
    }

    private async Task ExportSessionAsync(
        SessionExportWebCommand command,
        string nativePath,
        IAgentDeskMaintenanceLease lease,
        CancellationToken cancellationToken)
    {
        var document = await lease
            .ExportSessionAsync(new SessionId(command.SessionId), cancellationToken)
            .ConfigureAwait(false);
        await _documents.SaveAsync(nativePath, document, cancellationToken).ConfigureAwait(false);
        await _publish(new SessionExportedWebEvent(
                command.RequestId,
                command.SessionId,
                Path.GetFileName(Path.GetFullPath(nativePath))))
            .ConfigureAwait(false);
    }

    private async Task ImportSessionAsync(
        SessionImportWebCommand command,
        string nativePath,
        IAgentDeskMaintenanceLease lease,
        CancellationToken cancellationToken)
    {
        var document = await _documents.LoadAsync(nativePath, cancellationToken)
            .ConfigureAwait(false);
        var sessionId = await lease.ImportSessionAsync(document, cancellationToken)
            .ConfigureAwait(false);
        await _publish(new SessionImportedWebEvent(
                command.RequestId,
                sessionId.Value,
                lease.WorkspacePath))
            .ConfigureAwait(false);
    }

    private async Task CreateBackupAsync(
        BackupCreateWebCommand command,
        string nativePath,
        IAgentDeskMaintenanceLease lease,
        CancellationToken cancellationToken)
    {
        await lease.StopEngineAsync(cancellationToken).ConfigureAwait(false);
        var result = await _backups
            .CreateAsync(_options.DataDirectory, nativePath, cancellationToken)
            .ConfigureAwait(false);
        await _publish(new BackupCompletedWebEvent(
                command.RequestId,
                "create",
                result.FileCount,
                result.TotalBytes,
                RestartRequired: false))
            .ConfigureAwait(false);
    }

    private async Task RestoreBackupAsync(
        BackupRestoreWebCommand command,
        string nativePath,
        IAgentDeskMaintenanceLease lease,
        CancellationToken cancellationToken)
    {
        var backupPath = Path.GetFullPath(nativePath);
        if (IsWithin(_options.DataDirectory, backupPath))
        {
            throw new InvalidDataException("A backup inside the data directory cannot be restored.");
        }

        var parent = Directory.GetParent(_options.DataDirectory)?.FullName ??
            throw new InvalidDataException("The AgentDesk data directory is invalid.");
        Directory.CreateDirectory(parent);
        var preparedDirectory = Path.Combine(parent, $".agentdesk-prepared-{Guid.NewGuid():N}");
        try
        {
            var result = await _backups
                .RestoreAsync(backupPath, preparedDirectory, cancellationToken)
                .ConfigureAwait(false);
            await _prepareRestore(cancellationToken).ConfigureAwait(false);
            await lease.StopEngineAsync(cancellationToken).ConfigureAwait(false);
            ReplaceDataDirectory(preparedDirectory, _options.DataDirectory);
            await _publish(new BackupCompletedWebEvent(
                    command.RequestId,
                    "restore",
                    result.FileCount,
                    result.TotalBytes,
                    RestartRequired: true))
                .ConfigureAwait(false);
            await _restart(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(preparedDirectory))
            {
                Directory.Delete(preparedDirectory, recursive: true);
            }
        }
    }

    private async Task CheckUpdateAsync(
        UpdateCheckWebCommand command,
        CancellationToken cancellationToken)
    {
        if (_options.PackageMode is AgentDeskPackageMode.Msix)
        {
            await _publish(new UpdateStatusWebEvent(command.RequestId, "unsupported"))
                .ConfigureAwait(false);
            return;
        }

        await _publish(new UpdateStatusWebEvent(command.RequestId, "checking"))
            .ConfigureAwait(false);
        var available = await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
        await _publish(available is null
                ? new UpdateStatusWebEvent(command.RequestId, "up-to-date")
                : new UpdateStatusWebEvent(
                    command.RequestId,
                    "available",
                    available.Version.ToString()))
            .ConfigureAwait(false);
    }

    private async Task ApplyUpdateAsync(
        UpdateApplyWebCommand command,
        IAgentDeskMaintenanceLease lease,
        CancellationToken cancellationToken)
    {
        if (_options.PackageMode is AgentDeskPackageMode.Msix)
        {
            await _publish(new UpdateStatusWebEvent(command.RequestId, "unsupported"))
                .ConfigureAwait(false);
            return;
        }

        await lease.StopEngineAsync(cancellationToken).ConfigureAwait(false);
        await _updates.LaunchAsync(_options.ParentProcessId, cancellationToken)
            .ConfigureAwait(false);
        await _publish(new UpdateStatusWebEvent(command.RequestId, "launching"))
            .ConfigureAwait(false);
        await _exit(cancellationToken).ConfigureAwait(false);
    }

    private static bool RequiresFile(MaintenanceWebCommand command) =>
        command is SessionExportWebCommand or SessionImportWebCommand or
            BackupCreateWebCommand or BackupRestoreWebCommand;

    private static string OperationName(MaintenanceWebCommand command) => command switch
    {
        SessionExportWebCommand => "session-export",
        SessionImportWebCommand => "session-import",
        BackupCreateWebCommand => "backup-create",
        BackupRestoreWebCommand => "backup-restore",
        UpdateCheckWebCommand => "update-check",
        UpdateApplyWebCommand => "update-apply",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static void ReplaceDataDirectory(string preparedDirectory, string dataDirectory)
    {
        var parent = Directory.GetParent(dataDirectory)?.FullName ??
            throw new InvalidDataException("The AgentDesk data directory is invalid.");
        var previousDirectory = Path.Combine(parent, $".agentdesk-previous-{Guid.NewGuid():N}");
        var previousMoved = false;
        var replacementMoved = false;
        try
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Move(dataDirectory, previousDirectory);
                previousMoved = true;
            }
            Directory.Move(preparedDirectory, dataDirectory);
            replacementMoved = true;
            if (previousMoved)
            {
                Directory.Delete(previousDirectory, recursive: true);
                previousMoved = false;
            }
        }
        catch
        {
            if (replacementMoved && Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            if (previousMoved && Directory.Exists(previousDirectory) &&
                !Directory.Exists(dataDirectory))
            {
                Directory.Move(previousDirectory, dataDirectory);
            }
            throw;
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (!Path.IsPathRooted(relative) && relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
