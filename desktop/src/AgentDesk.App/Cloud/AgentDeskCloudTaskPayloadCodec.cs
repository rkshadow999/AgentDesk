using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentDesk.Cloud.Client;

namespace AgentDesk.App.Cloud;

public sealed record AgentDeskCloudRunnerJobClaim(
    string ClaimHandle,
    CloudRunnerJob Job)
{
    public string JobId => Job.JobId;

    public CloudRunnerJobIdentity Identity => Job.Identity;

    public override string ToString() =>
        $"{nameof(AgentDeskCloudRunnerJobClaim)} {{ ClaimHandle = {ClaimHandle}, JobId = {JobId} }}";
}

public sealed record AgentDeskCloudRunnerTask(
    string ClaimHandle,
    CloudRunnerJobIdentity Identity,
    string Task,
    DateTimeOffset LeaseExpiresAt)
{
    public string JobId => Identity.JobId;

    public string Kind => Identity.Kind;

    public string RequiredCapability => Identity.RequiredCapability;

    public string? AutomationId => Identity.AutomationId;

    public string? RunId => Identity.RunId;

    public override string ToString() =>
        $"{nameof(AgentDeskCloudRunnerTask)} {{ ClaimHandle = {ClaimHandle}, JobId = {JobId}, Kind = {Kind} }}";
}

internal sealed class AgentDeskCloudTaskPayloadCodec
{
    private const int SchemaVersion = 2;
    private const int MaximumTaskCharacters = 64 * 1024;
    private const int MaximumResultCharacters = 256 * 1024;
    private const int MaximumPayloadBytes = 512 * 1024;
    private const string SharedRunnerDeviceId = "shared-runner";

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AesGcmEnvelopeCodec _codec = new(MaximumPayloadBytes);

    public EncryptedEnvelope ProtectTask(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        CloudRunnerJobIdentity identity,
        string task)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != CloudRunnerPayloadKinds.Task)
        {
            throw new ArgumentException("A direct task identity is required.", nameof(identity));
        }
        return Protect(
            profile,
            recoveryKeyStore,
            Metadata(
                identity.Kind,
                identity.JobId,
                null,
                null,
                identity.RequiredCapability,
                leaseGeneration: null),
            task,
            MaximumTaskCharacters);
    }

    public EncryptedEnvelope ProtectAutomationTask(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        string automationId,
        string requiredCapability,
        string task) =>
        Protect(
            profile,
            recoveryKeyStore,
            Metadata(
                CloudRunnerPayloadKinds.Automation,
                null,
                ValidateIdentifier(automationId, 128, nameof(automationId)),
                null,
                ValidateIdentifier(requiredCapability, 64, nameof(requiredCapability)),
                leaseGeneration: null),
            task,
            MaximumTaskCharacters);

    public EncryptedEnvelope ProtectResult(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        CloudRunnerJobIdentity identity,
        string result)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.LeaseGeneration < 1)
        {
            throw new ArgumentException(
                "A claimed cloud runner identity is required.",
                nameof(identity));
        }
        return Protect(
            profile,
            recoveryKeyStore,
            Metadata(
                identity.ResultKind,
                identity.JobId,
                identity.AutomationId,
                identity.RunId,
                identity.RequiredCapability,
                identity.LeaseGeneration),
            result,
            MaximumResultCharacters);
    }

    public string UnprotectTask(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        CloudRunnerJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var metadata = job.Kind switch
        {
            CloudRunnerPayloadKinds.Task => Metadata(
                job.Kind,
                job.JobId,
                null,
                null,
                job.RequiredCapability,
                leaseGeneration: null),
            CloudRunnerPayloadKinds.Automation => Metadata(
                job.Kind,
                null,
                job.AutomationId,
                null,
                job.RequiredCapability,
                leaseGeneration: null),
            _ => throw new InvalidDataException("The cloud runner task payload is invalid."),
        };
        return Unprotect(
            profile,
            recoveryKeyStore,
            job.Envelope,
            metadata,
            MaximumTaskCharacters,
            "task");
    }

    public string UnprotectResult(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        CloudRunnerJobIdentity identity,
        EncryptedEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Unprotect(
            profile,
            recoveryKeyStore,
            envelope,
            Metadata(
                identity.ResultKind,
                identity.JobId,
                identity.AutomationId,
                identity.RunId,
                identity.RequiredCapability,
                identity.LeaseGeneration),
            MaximumResultCharacters,
            "result");
    }

    private EncryptedEnvelope Protect(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        PayloadMetadata metadata,
        string text,
        int maximumCharacters)
    {
        var validated = ValidateText(text, maximumCharacters, metadata.Kind);
        var key = ReadKey(profile, recoveryKeyStore);
        byte[]? plaintext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                new RunnerPayload(
                    SchemaVersion,
                    metadata.Kind,
                    metadata.JobId,
                    metadata.AutomationId,
                    metadata.RunId,
                    metadata.RequiredCapability,
                    metadata.LeaseGeneration,
                    validated),
                PayloadJsonOptions);
            return _codec.Encrypt(plaintext, key, Binding(profile, metadata));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private string Unprotect(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore,
        EncryptedEnvelope envelope,
        PayloadMetadata metadata,
        int maximumCharacters,
        string valueName)
    {
        var key = ReadKey(profile, recoveryKeyStore);
        byte[]? plaintext = null;
        try
        {
            plaintext = _codec.Decrypt(envelope, key, Binding(profile, metadata));
            var payload = JsonSerializer.Deserialize<RunnerPayload>(plaintext, PayloadJsonOptions);
            if (payload is null ||
                payload.SchemaVersion != SchemaVersion ||
                !string.Equals(payload.Kind, metadata.Kind, StringComparison.Ordinal) ||
                !string.Equals(payload.JobId, metadata.JobId, StringComparison.Ordinal) ||
                !string.Equals(payload.AutomationId, metadata.AutomationId, StringComparison.Ordinal) ||
                !string.Equals(payload.RunId, metadata.RunId, StringComparison.Ordinal) ||
                !string.Equals(
                    payload.RequiredCapability,
                    metadata.RequiredCapability,
                    StringComparison.Ordinal) ||
                payload.LeaseGeneration != metadata.LeaseGeneration)
            {
                throw new InvalidDataException("The cloud runner payload metadata is invalid.");
            }
            return ValidateText(payload.Text, maximumCharacters, valueName);
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or
            InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("The cloud runner payload is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static EnvelopeBinding Binding(
        CloudConnectionProfile profile,
        PayloadMetadata metadata)
    {
        var encoded = JsonSerializer.SerializeToUtf8Bytes(metadata, PayloadJsonOptions);
        try
        {
            var digest = SHA256.HashData(encoded);
            return new EnvelopeBinding(
                profile.TeamId!,
                SharedRunnerDeviceId,
                $"runner-v2-{Convert.ToHexStringLower(digest)}",
                revision: 1);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static PayloadMetadata Metadata(
        string kind,
        string? jobId,
        string? automationId,
        string? runId,
        string requiredCapability,
        long? leaseGeneration)
    {
        // Task ciphertext is created before a lease exists and stays bound to immutable job
        // metadata so an opaque server can legally redeliver it. Results bind the active
        // lease generation to fence stale executions without giving the server encryption keys.
        return new(
            kind,
            jobId,
            automationId,
            runId,
            requiredCapability,
            leaseGeneration);
    }

    private static byte[] ReadKey(
        CloudConnectionProfile profile,
        CredentialRecoveryKeyStore recoveryKeyStore)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recoveryKeyStore);
        if (profile.IsLocalOnly)
        {
            throw new AgentDeskCloudUnavailableException();
        }
        return recoveryKeyStore.GetOrCreate(RecoveryKeyReference.ForTeam(profile.TeamId!));
    }

    private static string ValidateIdentifier(string? value, int maximumCharacters, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The cloud runner identifier is invalid.", name);
        }
        return value;
    }

    private static string ValidateText(string? value, int maximumCharacters, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumCharacters ||
            value.Any(character => character is '\0'))
        {
            throw new ArgumentException($"The cloud runner {name} is invalid.", name);
        }
        return value;
    }

    private sealed record PayloadMetadata(
        string Kind,
        string? JobId,
        string? AutomationId,
        string? RunId,
        string RequiredCapability,
        long? LeaseGeneration);

    private sealed record RunnerPayload(
        int SchemaVersion,
        string Kind,
        string? JobId,
        string? AutomationId,
        string? RunId,
        string RequiredCapability,
        long? LeaseGeneration,
        string Text);
}
