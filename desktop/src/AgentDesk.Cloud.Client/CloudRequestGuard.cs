namespace AgentDesk.Cloud.Client;

internal static class CloudRequestGuard
{
    private const int AuthenticationTagSizeInBytes = 16;

    public static string Identifier(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The identifier contains unsupported characters.", parameterName);
        }

        return value;
    }

    public static EncryptedEnvelope Envelope(
        EncryptedEnvelope envelope,
        int maximumEnvelopeBytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(envelope, parameterName);
        var nonce = DecodeBase64(envelope.Nonce, 12, parameterName);
        if (nonce.Length != 12)
        {
            throw new ArgumentException("An AES-256-GCM nonce must contain exactly 12 bytes.", parameterName);
        }

        var ciphertext = DecodeBase64(envelope.Ciphertext, maximumEnvelopeBytes, parameterName);
        if (ciphertext.Length < AuthenticationTagSizeInBytes)
        {
            throw new ArgumentException("The encrypted envelope is missing its authentication tag.", parameterName);
        }

        return envelope;
    }

    public static string AutomationName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128)
        {
            throw new ArgumentException("The automation name cannot exceed 128 characters.", nameof(value));
        }

        return value;
    }

    private static byte[] DecodeBase64(string value, int maximumBytes, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var maximumEncodedLength = checked(((maximumBytes + 2) / 3) * 4);
        if (value.Length > maximumEncodedLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The encrypted envelope exceeds the configured limit.");
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length > maximumBytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The encrypted envelope exceeds the configured limit.");
            }
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The encrypted envelope is not valid Base64.", parameterName, exception);
        }
    }
}
