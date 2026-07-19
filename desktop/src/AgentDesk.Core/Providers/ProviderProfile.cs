using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentDesk.Core.Providers;

public sealed record ProviderProfile
{
    private const int MaximumBaseUrlLength = 2048;
    private const int MaximumModelLength = 256;

    [JsonConstructor]
    public ProviderProfile(
        string baseUrl,
        string model,
        ProviderBackend backend = ProviderBackend.ChatCompletions,
        bool allowInsecureTransport = false)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        Model = NormalizeModel(model);
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend));
        }

        Backend = backend;
        AllowInsecureTransport = allowInsecureTransport;
        CredentialName = BuildCredentialName(BaseUrl);
    }

    public string BaseUrl { get; }

    public string Model { get; }

    public ProviderBackend Backend { get; }

    public bool AllowInsecureTransport { get; }

    [JsonIgnore]
    public bool CanSendCredentials =>
        BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        AllowInsecureTransport;

    [JsonIgnore]
    public string CredentialName { get; }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var candidate = baseUrl.Trim();
        if (candidate.Length > MaximumBaseUrlLength ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("The provider Base URL is invalid.", nameof(baseUrl));
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = uri.AbsolutePath.TrimEnd('/'),
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    private static string NormalizeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var normalized = model.Trim();
        if (normalized.Length > MaximumModelLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The provider model is invalid.", nameof(model));
        }

        return normalized;
    }

    private static string BuildCredentialName(string normalizedBaseUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedBaseUrl));
        return $"providers/{Convert.ToHexStringLower(hash)}.api_key";
    }
}
