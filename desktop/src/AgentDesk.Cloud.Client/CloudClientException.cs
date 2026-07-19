using System.Net;

namespace AgentDesk.Cloud.Client;

public enum CloudClientErrorKind
{
    Credential,
    Authentication,
    Authorization,
    Validation,
    NotFound,
    Conflict,
    RateLimited,
    Server,
    Transport,
    Timeout,
    ResponseTooLarge,
    InvalidResponse,
}

public sealed class CloudClientException : Exception
{
    internal CloudClientException(
        CloudClientErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public CloudClientErrorKind Kind { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
