namespace AgentDesk.Cloud.Client;

public sealed class CloudConnectionOptions
{
    public const int DefaultMaximumEnvelopeBytes = 16 * 1024 * 1024;
    public const int DefaultMaximumResponseBytes =
        ((DefaultMaximumEnvelopeBytes + 2) / 3 * 4) + JsonEnvelopeOverheadBytes;

    internal const int JsonEnvelopeOverheadBytes = 64 * 1024;

    private const int MaximumPermittedEnvelopeBytes = 64 * 1024 * 1024;
    private const int MaximumPermittedResponseBytes =
        ((MaximumPermittedEnvelopeBytes + 2) / 3 * 4) + JsonEnvelopeOverheadBytes;

    public CloudConnectionOptions(
        Uri baseUri,
        TimeSpan? requestTimeout = null,
        int maximumEnvelopeBytes = DefaultMaximumEnvelopeBytes,
        int? maximumResponseBytes = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("The cloud endpoint must be an absolute URI.", nameof(baseUri));
        }
        if (baseUri.Scheme != Uri.UriSchemeHttps &&
            !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback))
        {
            throw new ArgumentException(
                "The cloud endpoint must use HTTPS. HTTP is permitted only for loopback testing.",
                nameof(baseUri));
        }
        if (!string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "The cloud endpoint cannot contain credentials, a query, or a fragment.",
                nameof(baseUri));
        }

        var timeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The request timeout must be between zero and five minutes.");
        }
        if (maximumEnvelopeBytes is < 16 or > MaximumPermittedEnvelopeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEnvelopeBytes),
                $"The envelope limit must be between 16 and {MaximumPermittedEnvelopeBytes} bytes.");
        }
        var responseBytes = maximumResponseBytes ?? GetMaximumSerializedEnvelopeBytes(
            maximumEnvelopeBytes);
        if (responseBytes is < 1 or > MaximumPermittedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseBytes),
                $"The response limit must be between 1 and {MaximumPermittedResponseBytes} bytes.");
        }

        BaseUri = EnsureTrailingSlash(baseUri);
        RequestTimeout = timeout;
        MaximumEnvelopeBytes = maximumEnvelopeBytes;
        MaximumResponseBytes = responseBytes;
    }

    public Uri BaseUri { get; }

    public TimeSpan RequestTimeout { get; }

    public int MaximumEnvelopeBytes { get; }

    public int MaximumResponseBytes { get; }

    internal int MaximumSerializedEnvelopeBytes =>
        GetMaximumSerializedEnvelopeBytes(MaximumEnvelopeBytes);

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? value
            : new Uri(value.AbsoluteUri + '/', UriKind.Absolute);

    private static int GetMaximumSerializedEnvelopeBytes(int maximumEnvelopeBytes) =>
        checked(((maximumEnvelopeBytes + 2) / 3 * 4) + JsonEnvelopeOverheadBytes);
}
