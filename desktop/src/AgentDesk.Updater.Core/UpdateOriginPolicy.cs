namespace AgentDesk.Updater.Core;

public sealed class UpdateOriginPolicy
{
    private readonly HashSet<string> _trustedHosts;

    public UpdateOriginPolicy(IEnumerable<string> trustedHosts)
    {
        ArgumentNullException.ThrowIfNull(trustedHosts);
        _trustedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trustedHost in trustedHosts)
        {
            if (string.IsNullOrWhiteSpace(trustedHost) ||
                trustedHost.Contains('*', StringComparison.Ordinal) ||
                !Uri.CheckHostName(trustedHost.Trim()).Equals(UriHostNameType.Dns))
            {
                throw new ArgumentException("Trusted update hosts must be exact DNS names.", nameof(trustedHosts));
            }

            _trustedHosts.Add(trustedHost.Trim().TrimEnd('.'));
        }

        if (_trustedHosts.Count == 0)
        {
            throw new ArgumentException("At least one trusted update host is required.", nameof(trustedHosts));
        }
    }

    public static UpdateOriginPolicy GitHub { get; } = new(
        [
            "github.com",
            "api.github.com",
            "raw.githubusercontent.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
            "github-releases.githubusercontent.com",
        ]);

    /// <summary>
    /// Self-hosted community update feed (rkshadow 74 nginx).
    /// HTTPS only; host must match exactly (no wildcards).
    /// </summary>
    public static UpdateOriginPolicy SelfHosted { get; } = new(
        [
            "update.rkshadow.com",
        ]);

    /// <summary>
    /// Accept either GitHub Release feeds or the self-hosted update host.
    /// </summary>
    public static UpdateOriginPolicy Default { get; } = new(
        [
            "github.com",
            "api.github.com",
            "raw.githubusercontent.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com",
            "github-releases.githubusercontent.com",
            "update.rkshadow.com",
        ]);

    public void EnsureAllowedInitialUri(Uri uri) => EnsureAllowed(uri, allowQuery: false);

    internal void EnsureAllowedRedirectUri(Uri uri) => EnsureAllowed(uri, allowQuery: true);

    private void EnsureAllowed(Uri uri, bool allowQuery)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (!allowQuery && !string.IsNullOrEmpty(uri.Query)) ||
            !_trustedHosts.Contains(uri.IdnHost.TrimEnd('.')))
        {
            throw new UpdateSecurityException("The update URI is not a trusted HTTPS origin.");
        }
    }
}
