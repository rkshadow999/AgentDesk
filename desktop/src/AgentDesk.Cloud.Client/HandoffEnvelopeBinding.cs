namespace AgentDesk.Cloud.Client;

public sealed record HandoffEnvelopeBinding
{
    public HandoffEnvelopeBinding(
        string teamId,
        string sourceDeviceId,
        string targetDeviceId,
        string sessionId,
        string handoffId,
        int revision)
    {
        TeamId = ValidateIdentifier(teamId, nameof(teamId));
        SourceDeviceId = ValidateIdentifier(sourceDeviceId, nameof(sourceDeviceId));
        TargetDeviceId = ValidateIdentifier(targetDeviceId, nameof(targetDeviceId));
        SessionId = ValidateIdentifier(sessionId, nameof(sessionId));
        HandoffId = ValidateIdentifier(handoffId, nameof(handoffId));
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "The revision must be positive.");
        }

        Revision = revision;
    }

    public string TeamId { get; }

    public string SourceDeviceId { get; }

    public string TargetDeviceId { get; }

    public string SessionId { get; }

    public string HandoffId { get; }

    public int Revision { get; }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128)
        {
            throw new ArgumentException("The handoff identifier cannot exceed 128 characters.", parameterName);
        }

        return value;
    }
}
