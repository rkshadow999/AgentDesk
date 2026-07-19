namespace AgentDesk.Cloud.Client;

public sealed class EncryptedEnvelope
{
    public const string Aes256GcmAlgorithm = "AES-256-GCM";

    public EncryptedEnvelope(string algorithm, string nonce, string ciphertext)
    {
        if (!string.Equals(algorithm, Aes256GcmAlgorithm, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only AES-256-GCM envelopes are supported.", nameof(algorithm));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);

        Algorithm = algorithm;
        Nonce = nonce;
        Ciphertext = ciphertext;
    }

    public string Algorithm { get; }

    public string Nonce { get; }

    public string Ciphertext { get; }

    public override string ToString() => $"EncryptedEnvelope {{ Algorithm = {Algorithm} }}";
}
