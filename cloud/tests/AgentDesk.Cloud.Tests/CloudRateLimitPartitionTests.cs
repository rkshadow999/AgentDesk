using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace AgentDesk.Cloud.Tests;

public sealed class CloudRateLimitPartitionTests
{
    [Fact]
    public void AnonymousPartitionNormalizesIpv4MappedAddresses()
    {
        var mapped = new DefaultHttpContext();
        mapped.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.44");
        var ipv4 = new DefaultHttpContext();
        ipv4.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.44");

        Assert.Equal(
            CloudRateLimitPartition.GetKey(ipv4),
            CloudRateLimitPartition.GetKey(mapped));
    }

    [Fact]
    public void AuthenticatedPartitionIncludesBothTeamAndSubject()
    {
        var first = Authenticated("team-1", "device-1");
        var otherSubject = Authenticated("team-1", "device-2");
        var otherTeam = Authenticated("team-2", "device-1");

        Assert.NotEqual(
            CloudRateLimitPartition.GetKey(first),
            CloudRateLimitPartition.GetKey(otherSubject));
        Assert.NotEqual(
            CloudRateLimitPartition.GetKey(first),
            CloudRateLimitPartition.GetKey(otherTeam));
    }

    private static DefaultHttpContext Authenticated(string teamId, string subjectId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                [
                    new Claim("agentdesk:team", teamId),
                    new Claim(ClaimTypes.NameIdentifier, subjectId),
                    new Claim(ClaimTypes.Role, CloudRoles.Device),
                ],
                authenticationType: "test"));
        return context;
    }
}
