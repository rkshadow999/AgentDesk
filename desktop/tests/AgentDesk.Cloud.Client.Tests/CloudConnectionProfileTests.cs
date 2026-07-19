using System.Text.Json;
using AgentDesk.Cloud.Client;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class CloudConnectionProfileTests
{
    [Fact]
    public void DefaultProfileIsLocalOnlyAndContainsNoRemoteMetadata()
    {
        var profile = new CloudConnectionProfile();

        Assert.True(profile.IsLocalOnly);
        Assert.Null(profile.BaseUri);
        Assert.Null(profile.TeamId);
        Assert.Null(profile.DeviceId);
        Assert.Null(profile.AccessTokenCredentialName);
        Assert.DoesNotContain("token", profile.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://cloud.agentdesk.example/root")]
    [InlineData("http://localhost:5050")]
    [InlineData("http://127.0.0.1:5050")]
    public void RemoteProfileAcceptsHttpsAndLoopback(string endpoint)
    {
        var profile = new CloudConnectionProfile(
            new Uri(endpoint),
            "team-1",
            "device-1");

        Assert.False(profile.IsLocalOnly);
        Assert.EndsWith("/", profile.BaseUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("team-1", profile.TeamId);
        Assert.Equal("device-1", profile.DeviceId);
        Assert.StartsWith(
            "cloud/access/",
            profile.AccessTokenCredentialName,
            StringComparison.Ordinal);
        Assert.Equal(profile.BaseUri, profile.CreateConnectionOptions().BaseUri);
    }

    [Theory]
    [InlineData("http://cloud.agentdesk.example/")]
    [InlineData("ftp://localhost/")]
    public void RemoteProfileRejectsUnsafeTransport(string endpoint)
    {
        Assert.Throws<ArgumentException>(
            () => new CloudConnectionProfile(
                new Uri(endpoint),
                "team-1",
                "device-1"));
    }

    [Theory]
    [InlineData("../team", "device-1")]
    [InlineData("team-1", "device/1")]
    [InlineData("团队", "device-1")]
    public void RemoteProfileRejectsInvalidIdentifiers(string teamId, string deviceId)
    {
        Assert.Throws<ArgumentException>(
            () => new CloudConnectionProfile(
                new Uri("https://cloud.agentdesk.example/"),
                teamId,
                deviceId));
    }

    [Fact]
    public void CredentialTargetIsStableButDoesNotExposeProfileIdentifiers()
    {
        var first = new CloudConnectionProfile(
            new Uri("https://cloud.agentdesk.example/"),
            "sensitive-team-name",
            "sensitive-device-name");
        var equivalent = new CloudConnectionProfile(
            new Uri("https://CLOUD.agentdesk.example"),
            "sensitive-team-name",
            "sensitive-device-name");

        Assert.Equal(first.AccessTokenCredentialName, equivalent.AccessTokenCredentialName);
        Assert.DoesNotContain(
            "sensitive-team-name",
            first.AccessTokenCredentialName,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sensitive-device-name",
            first.AccessTokenCredentialName,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "credential",
            JsonSerializer.Serialize(first),
            StringComparison.OrdinalIgnoreCase);
    }
}
