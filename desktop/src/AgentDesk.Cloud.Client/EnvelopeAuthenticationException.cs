using System.Security.Cryptography;

namespace AgentDesk.Cloud.Client;

public sealed class EnvelopeAuthenticationException : CryptographicException
{
    internal EnvelopeAuthenticationException(CryptographicException innerException)
        : base("The encrypted envelope could not be authenticated.", innerException)
    {
    }
}
