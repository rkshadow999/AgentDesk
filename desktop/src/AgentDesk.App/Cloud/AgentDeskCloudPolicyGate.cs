using AgentDesk.Cloud.Client;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Cloud;

public enum AgentDeskCloudPolicyMode
{
    LocalOnly,
    RemoteUnverified,
    RemoteVerified,
}

public readonly record struct AgentDeskCloudPolicyVersion(long Value);

public sealed record AgentDeskCloudPolicySnapshot(
    IReadOnlyList<ExecutionProfile> AllowedExecutionProfiles,
    bool RemoteRunnerEnabled,
    bool UiAutomationEnabled,
    int MaximumConcurrentJobs,
    IReadOnlyList<string> AllowedPluginPublishers)
{
    public AgentDeskCloudPolicySnapshot Validate()
    {
        ArgumentNullException.ThrowIfNull(AllowedExecutionProfiles);
        ArgumentNullException.ThrowIfNull(AllowedPluginPublishers);
        if (AllowedExecutionProfiles.Count is < 1 or > 2 ||
            AllowedExecutionProfiles.Distinct().Count() != AllowedExecutionProfiles.Count)
        {
            throw new ArgumentException("The cloud execution profiles are invalid.");
        }
        if (MaximumConcurrentJobs is < 1 or > CloudPolicyLimits.MaximumConcurrentJobs)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentJobs));
        }
        if (AllowedPluginPublishers.Count > CloudPolicyLimits.MaximumPluginPublishers ||
            AllowedPluginPublishers.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > CloudPolicyLimits.MaximumPublisherIdCharacters) ||
            AllowedPluginPublishers.Distinct(StringComparer.Ordinal).Count() !=
                AllowedPluginPublishers.Count)
        {
            throw new ArgumentException("The cloud plugin publisher policy is invalid.");
        }
        return this;
    }

    internal static AgentDeskCloudPolicySnapshot FromPolicy(CloudTeamPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var profiles = policy.AllowedExecutionProfiles.Select(value => value switch
        {
            "NativeProtected" => ExecutionProfile.NativeProtected,
            "WslStrict" => ExecutionProfile.WslStrict,
            _ => throw new InvalidDataException("The cloud execution profile is invalid."),
        }).ToArray();
        return new AgentDeskCloudPolicySnapshot(
            profiles,
            policy.RemoteRunnerEnabled,
            policy.UiAutomationEnabled,
            policy.MaximumConcurrentJobs,
            policy.AllowedPluginPublishers).Validate();
    }
}

public sealed class AgentDeskCloudPolicyGate
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private AgentDeskCloudPolicyMode _mode = AgentDeskCloudPolicyMode.LocalOnly;
    private HashSet<ExecutionProfile> _allowedExecutionProfiles = [];
    private bool _remoteRunnerEnabled;
    private bool _uiAutomationEnabled;
    private int _maximumConcurrentJobs;
    private HashSet<string> _allowedPluginPublishers = new(StringComparer.Ordinal);
    private long _version;

    public AgentDeskCloudPolicyMode Mode
    {
        get
        {
            lock (_sync)
            {
                return _mode;
            }
        }
    }

    public bool AllowsRemoteRunner
    {
        get
        {
            lock (_sync)
            {
                return _mode is AgentDeskCloudPolicyMode.RemoteVerified &&
                    _remoteRunnerEnabled;
            }
        }
    }

    public AgentDeskCloudPolicyVersion CaptureVersion()
    {
        lock (_sync)
        {
            return new AgentDeskCloudPolicyVersion(_version);
        }
    }

    public bool IsCurrent(AgentDeskCloudPolicyVersion version)
    {
        lock (_sync)
        {
            return _version == version.Value;
        }
    }

    public int MaximumConcurrentJobs
    {
        get
        {
            lock (_sync)
            {
                return _mode is AgentDeskCloudPolicyMode.RemoteVerified
                    ? _maximumConcurrentJobs
                    : 0;
            }
        }
    }

    public void ApplyLocalOnlyProfile() =>
        ApplyLocalOnlyProfileAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task ApplyLocalOnlyProfileAsync(CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_mode is AgentDeskCloudPolicyMode.LocalOnly && RemotePolicyIsClearUnsafe())
                {
                    return;
                }
                _mode = AgentDeskCloudPolicyMode.LocalOnly;
                ClearRemotePolicyUnsafe();
                AdvanceVersionUnsafe();
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public void ApplyRemoteProfile() =>
        ApplyRemoteProfileAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task ApplyRemoteProfileAsync(CancellationToken cancellationToken = default)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_mode is AgentDeskCloudPolicyMode.RemoteUnverified && RemotePolicyIsClearUnsafe())
                {
                    return;
                }
                _mode = AgentDeskCloudPolicyMode.RemoteUnverified;
                ClearRemotePolicyUnsafe();
                AdvanceVersionUnsafe();
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public void ApplyPolicy(CloudTeamPolicy policy) =>
        ApplyPolicy(AgentDeskCloudPolicySnapshot.FromPolicy(policy));

    public Task ApplyPolicyAsync(
        CloudTeamPolicy policy,
        CancellationToken cancellationToken = default) =>
        ApplyPolicyAsync(AgentDeskCloudPolicySnapshot.FromPolicy(policy), cancellationToken);

    public void ApplyPolicy(AgentDeskCloudPolicySnapshot policy) =>
        ApplyPolicyAsync(policy, CancellationToken.None).GetAwaiter().GetResult();

    public async Task ApplyPolicyAsync(
        AgentDeskCloudPolicySnapshot policy,
        CancellationToken cancellationToken = default)
    {
        var validated = policy.Validate();
        var allowedExecutionProfiles = validated.AllowedExecutionProfiles.ToHashSet();
        var allowedPluginPublishers = validated.AllowedPluginPublishers.ToHashSet(
            StringComparer.Ordinal);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_mode is AgentDeskCloudPolicyMode.RemoteVerified &&
                    _allowedExecutionProfiles.SetEquals(allowedExecutionProfiles) &&
                    _remoteRunnerEnabled == validated.RemoteRunnerEnabled &&
                    _uiAutomationEnabled == validated.UiAutomationEnabled &&
                    _maximumConcurrentJobs == validated.MaximumConcurrentJobs &&
                    _allowedPluginPublishers.SetEquals(allowedPluginPublishers))
                {
                    return;
                }

                _allowedExecutionProfiles = allowedExecutionProfiles;
                _remoteRunnerEnabled = validated.RemoteRunnerEnabled;
                _uiAutomationEnabled = validated.UiAutomationEnabled;
                _maximumConcurrentJobs = validated.MaximumConcurrentJobs;
                _allowedPluginPublishers = allowedPluginPublishers;
                _mode = AgentDeskCloudPolicyMode.RemoteVerified;
                AdvanceVersionUnsafe();
            }
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public async Task<T?> ExecuteIfCurrentAsync<T>(
        AgentDeskCloudPolicyVersion expectedVersion,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrent(expectedVersion))
            {
                return null;
            }
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public bool AllowsExecutionProfile(ExecutionProfile profile)
    {
        lock (_sync)
        {
            return _mode is AgentDeskCloudPolicyMode.LocalOnly ||
                (_mode is AgentDeskCloudPolicyMode.RemoteVerified &&
                    _allowedExecutionProfiles.Contains(profile));
        }
    }

    public bool AllowsWindowsAutomation(bool localEnabled)
    {
        if (!localEnabled)
        {
            return false;
        }
        lock (_sync)
        {
            return _mode is AgentDeskCloudPolicyMode.LocalOnly ||
                (_mode is AgentDeskCloudPolicyMode.RemoteVerified && _uiAutomationEnabled);
        }
    }

    public bool AllowsPluginPublisher(string publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        lock (_sync)
        {
            return _mode is AgentDeskCloudPolicyMode.LocalOnly ||
                (_mode is AgentDeskCloudPolicyMode.RemoteVerified &&
                    _allowedPluginPublishers.Contains(publisher));
        }
    }

    private void ClearRemotePolicyUnsafe()
    {
        _allowedExecutionProfiles = [];
        _remoteRunnerEnabled = false;
        _uiAutomationEnabled = false;
        _maximumConcurrentJobs = 0;
        _allowedPluginPublishers = new HashSet<string>(StringComparer.Ordinal);
    }

    private bool RemotePolicyIsClearUnsafe() =>
        _allowedExecutionProfiles.Count == 0 &&
        !_remoteRunnerEnabled &&
        !_uiAutomationEnabled &&
        _maximumConcurrentJobs == 0 &&
        _allowedPluginPublishers.Count == 0;

    private void AdvanceVersionUnsafe()
    {
        _version = checked(_version + 1);
    }
}
