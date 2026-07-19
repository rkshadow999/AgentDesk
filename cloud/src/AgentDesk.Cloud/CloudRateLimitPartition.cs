namespace AgentDesk.Cloud;

internal static class CloudRateLimitPartition
{
    public static string GetKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.Identity?.IsAuthenticated is true)
        {
            var identity = context.User.CloudIdentity();
            return $"authenticated:{identity.TeamId.Length}:{identity.TeamId}:{identity.SubjectId.Length}:{identity.SubjectId}";
        }

        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "anonymous:unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        return $"anonymous:{address.ToString().ToLowerInvariant()}";
    }
}
