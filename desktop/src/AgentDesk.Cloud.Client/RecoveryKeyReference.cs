namespace AgentDesk.Cloud.Client;

public sealed record RecoveryKeyReference
{
    private const string TeamSharedScope = "team-shared";

    public RecoveryKeyReference(string teamId, string deviceId)
    {
        TeamId = ValidateIdentifier(teamId, nameof(teamId));
        DeviceId = ValidateIdentifier(deviceId, nameof(deviceId));
    }

    public string TeamId { get; }

    public string DeviceId { get; }

    public static RecoveryKeyReference ForTeam(string teamId) =>
        new(teamId, TeamSharedScope);

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128)
        {
            throw new ArgumentException("The recovery key identifier cannot exceed 128 characters.", parameterName);
        }

        return value;
    }
}
