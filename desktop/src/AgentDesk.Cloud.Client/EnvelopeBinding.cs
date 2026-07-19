namespace AgentDesk.Cloud.Client;

public sealed record EnvelopeBinding
{
    public EnvelopeBinding(string teamId, string deviceId, string sessionId, int revision)
    {
        TeamId = ValidateIdentifier(teamId, nameof(teamId));
        DeviceId = ValidateIdentifier(deviceId, nameof(deviceId));
        SessionId = ValidateIdentifier(sessionId, nameof(sessionId));
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision), "The revision must be positive.");
        }

        Revision = revision;
    }

    public string TeamId { get; }

    public string DeviceId { get; }

    public string SessionId { get; }

    public int Revision { get; }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128)
        {
            throw new ArgumentException("The envelope identifier cannot exceed 128 characters.", parameterName);
        }

        return value;
    }
}
