using System.Diagnostics;
using System.Security.Cryptography;
using AgentDesk.Updater.Core;

namespace AgentDesk.App.Maintenance;

public sealed class AgentDeskUpdateOptions
{
    public AgentDeskUpdateOptions(
        Uri updaterManifestUri,
        Uri updaterSignatureUri,
        Uri applicationManifestUri,
        Uri applicationSignatureUri,
        ReadOnlyMemory<byte> publicKeySubjectPublicKeyInfo,
        SemanticVersion installedVersion,
        UpdateArchitecture architecture,
        string stateDirectory,
        string installationDirectory,
        bool allowPrerelease,
        IReadOnlyList<string> restartArguments)
    {
        ArgumentNullException.ThrowIfNull(updaterManifestUri);
        ArgumentNullException.ThrowIfNull(updaterSignatureUri);
        ArgumentNullException.ThrowIfNull(applicationManifestUri);
        ArgumentNullException.ThrowIfNull(applicationSignatureUri);
        ArgumentNullException.ThrowIfNull(restartArguments);
        UpdateOriginPolicy.Default.EnsureAllowedInitialUri(updaterManifestUri);
        UpdateOriginPolicy.Default.EnsureAllowedInitialUri(updaterSignatureUri);
        UpdateOriginPolicy.Default.EnsureAllowedInitialUri(applicationManifestUri);
        UpdateOriginPolicy.Default.EnsureAllowedInitialUri(applicationSignatureUri);
        if (publicKeySubjectPublicKeyInfo.Length is < 32 or > 1024)
        {
            throw new UpdateSecurityException("The pinned update public key size is invalid.");
        }
        if (restartArguments.Count > 128 || restartArguments.Any(argument =>
                argument is null || argument.Length > 8192 || argument.Any(char.IsControl)))
        {
            throw new UpdateSecurityException("The update restart arguments are unsafe.");
        }

        var state = UpdatePathSafety.FullPath(stateDirectory);
        var installation = UpdatePathSafety.FullPath(installationDirectory);
        if (!string.Equals(
                Path.GetPathRoot(state),
                Path.GetPathRoot(installation),
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, installation, StringComparison.OrdinalIgnoreCase) ||
            UpdatePathSafety.IsContained(state, installation) ||
            UpdatePathSafety.IsContained(installation, state))
        {
            throw new UpdateSecurityException(
                "The update state and installation directories must be separate peers on one volume.");
        }

        UpdaterManifestUri = updaterManifestUri;
        UpdaterSignatureUri = updaterSignatureUri;
        ApplicationManifestUri = applicationManifestUri;
        ApplicationSignatureUri = applicationSignatureUri;
        PublicKeySubjectPublicKeyInfo = publicKeySubjectPublicKeyInfo.ToArray();
        InstalledVersion = installedVersion;
        Architecture = architecture;
        StateDirectory = state;
        InstallationDirectory = installation;
        AllowPrerelease = allowPrerelease;
        RestartArguments = restartArguments.ToArray();
    }

    public Uri UpdaterManifestUri { get; }

    public Uri UpdaterSignatureUri { get; }

    public Uri ApplicationManifestUri { get; }

    public Uri ApplicationSignatureUri { get; }

    public ReadOnlyMemory<byte> PublicKeySubjectPublicKeyInfo { get; }

    public SemanticVersion InstalledVersion { get; }

    public UpdateArchitecture Architecture { get; }

    public string StateDirectory { get; }

    public string InstallationDirectory { get; }

    public bool AllowPrerelease { get; }

    public IReadOnlyList<string> RestartArguments { get; }

    public override string ToString() =>
        $"AgentDeskUpdateOptions {{ Architecture = {Architecture}, InstalledVersion = {InstalledVersion} }}";
}

public sealed record AgentDeskUpdateAvailability(SemanticVersion Version);

public sealed record AgentDeskUpdateProcessStart(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments);

public interface IAgentDeskUpdateStager
{
    Task<StagedUpdate?> CheckAndStageAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAgentDeskUpdateChecker
{
    Task<AgentDeskUpdateAvailability?> CheckAsync(
        CancellationToken cancellationToken = default);
}

public interface IAgentDeskUpdateProcessLauncher
{
    void Start(AgentDeskUpdateProcessStart start);
}

public sealed class AgentDeskUpdateCoordinator : IAgentDeskUpdateChecker, IDisposable
{
    private readonly AgentDeskUpdateOptions _options;
    private readonly IAgentDeskUpdateStager _stager;
    private readonly IAgentDeskUpdateProcessLauncher _launcher;
    private readonly bool _ownsStager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StagedUpdate? _staged;
    private bool _launched;
    private bool _disposed;

    public AgentDeskUpdateCoordinator(AgentDeskUpdateOptions options)
        : this(
            options,
            new AgentDeskPortableUpdateStager(),
            new AgentDeskUpdateProcessLauncher(),
            ownsStager: true)
    {
    }

    public AgentDeskUpdateCoordinator(
        AgentDeskUpdateOptions options,
        IAgentDeskUpdateStager stager,
        IAgentDeskUpdateProcessLauncher launcher)
        : this(options, stager, launcher, ownsStager: false)
    {
    }

    private AgentDeskUpdateCoordinator(
        AgentDeskUpdateOptions options,
        IAgentDeskUpdateStager stager,
        IAgentDeskUpdateProcessLauncher launcher,
        bool ownsStager)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _stager = stager ?? throw new ArgumentNullException(nameof(stager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _ownsStager = ownsStager;
    }

    public async Task<AgentDeskUpdateAvailability?> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_launched)
            {
                throw new InvalidOperationException("The staged updater has already been launched.");
            }
            if (_staged is not null)
            {
                return new AgentDeskUpdateAvailability(_staged.Manifest.Version);
            }

            var publicKey = _options.PublicKeySubjectPublicKeyInfo.ToArray();
            try
            {
                _staged = await _stager.CheckAndStageAsync(
                    new UpdateCheckRequest(
                        _options.UpdaterManifestUri,
                        _options.UpdaterSignatureUri,
                        publicKey,
                        _options.InstalledVersion,
                        _options.Architecture,
                        _options.AllowPrerelease,
                        _options.StateDirectory,
                        ExpectedProduct: "AgentDesk.Updater"),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
            return _staged is null
                ? null
                : new AgentDeskUpdateAvailability(_staged.Manifest.Version);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task LaunchAsync(
        int parentProcessId,
        CancellationToken cancellationToken = default)
    {
        if (parentProcessId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parentProcessId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_launched)
            {
                throw new InvalidOperationException("The staged updater has already been launched.");
            }
            var staged = _staged ??
                throw new InvalidOperationException("No trusted AgentDesk updater has been staged.");

            var executable = Path.Combine(
                staged.PayloadDirectory,
                staged.Asset.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            UpdatePathSafety.EnsureContained(
                staged.PayloadDirectory,
                executable,
                "staged updater entry point");
            UpdatePathSafety.EnsureNoReparsePoints(staged.PayloadDirectory);
            if (!File.Exists(executable) ||
                (File.GetAttributes(executable) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UpdateSecurityException("The staged updater entry point is missing or unsafe.");
            }

            var publicKeyPath = await WritePinnedPublicKeyAsync(cancellationToken)
                .ConfigureAwait(false);
            var arguments = BuildArguments(publicKeyPath, parentProcessId);
            _launcher.Start(new AgentDeskUpdateProcessStart(
                executable,
                staged.PayloadDirectory,
                arguments));
            _launched = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_ownsStager && _stager is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _gate.Dispose();
    }

    private async Task<string> WritePinnedPublicKeyAsync(CancellationToken cancellationToken)
    {
        var trustDirectory = Path.Combine(_options.StateDirectory, "trust");
        Directory.CreateDirectory(trustDirectory);
        UpdatePathSafety.EnsureNoReparsePoints(trustDirectory);
        var destination = Path.Combine(trustDirectory, "AgentDesk-update-public-key.spki");
        if (File.Exists(destination) &&
            (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UpdateSecurityException("The pinned update public key path is unsafe.");
        }

        var temporary = $"{destination}.tmp-{Guid.NewGuid():N}";
        var publicKey = _options.PublicKeySubjectPublicKeyInfo.ToArray();
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(publicKey, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private IReadOnlyList<string> BuildArguments(string publicKeyPath, int parentProcessId)
    {
        var arguments = new List<string>
        {
            "apply",
            "--manifest-uri", _options.ApplicationManifestUri.AbsoluteUri,
            "--signature-uri", _options.ApplicationSignatureUri.AbsoluteUri,
            "--public-key-file", publicKeyPath,
            "--current-version", _options.InstalledVersion.ToString(),
            "--architecture", _options.Architecture is UpdateArchitecture.Arm64 ? "arm64" : "x64",
            "--state-directory", _options.StateDirectory,
            "--install-directory", _options.InstallationDirectory,
            "--parent-process-id", parentProcessId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--parent-exit-timeout-seconds", "120",
        };
        if (_options.AllowPrerelease)
        {
            arguments.Add("--allow-prerelease");
        }
        foreach (var argument in _options.RestartArguments)
        {
            arguments.Add("--restart-argument");
            arguments.Add(argument);
        }
        return arguments.ToArray();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed class AgentDeskPortableUpdateStager : IAgentDeskUpdateStager, IDisposable
{
    private readonly PortableUpdateService _service = new(UpdateOriginPolicy.Default);

    public Task<StagedUpdate?> CheckAndStageAsync(
        UpdateCheckRequest request,
        CancellationToken cancellationToken = default) =>
        _service.CheckAndStageAsync(request, cancellationToken);

    public void Dispose() => _service.Dispose();
}

public sealed class AgentDeskUpdateProcessLauncher : IAgentDeskUpdateProcessLauncher
{
    public void Start(AgentDeskUpdateProcessStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var information = new ProcessStartInfo
        {
            FileName = start.ExecutablePath,
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in start.Arguments)
        {
            information.ArgumentList.Add(argument);
        }
        if (Process.Start(information) is null)
        {
            throw new InvalidOperationException("The AgentDesk updater process could not be started.");
        }
    }
}
