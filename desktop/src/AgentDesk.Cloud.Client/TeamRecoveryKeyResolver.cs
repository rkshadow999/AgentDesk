namespace AgentDesk.Cloud.Client;

internal static class TeamRecoveryKeyResolver
{
    public static byte[] GetOrCreate(
        IRecoveryKeyStore store,
        CloudConnectionProfile profile)
    {
        var existing = Read(store, profile);
        return existing ?? store.GetOrCreate(RecoveryKeyReference.ForTeam(profile.TeamId!));
    }

    public static byte[]? Read(
        IRecoveryKeyStore store,
        CloudConnectionProfile profile)
    {
        var teamReference = RecoveryKeyReference.ForTeam(profile.TeamId!);
        var teamKey = store.Read(teamReference);
        if (teamKey is not null)
        {
            return teamKey;
        }

        var legacyKey = store.Read(new RecoveryKeyReference(
            profile.TeamId!,
            profile.DeviceId!));
        if (legacyKey is null)
        {
            return null;
        }

        store.Save(teamReference, legacyKey);
        return legacyKey;
    }
}
