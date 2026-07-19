using AgentDesk.Cloud.Client;
using AgentDesk.Core.Security;

namespace AgentDesk.Cloud.Client.Tests;

public sealed class CredentialCloudAccessTokenProviderTests
{
    private static readonly CloudConnectionProfile Profile = new(
        new Uri("https://cloud.agentdesk.example/"),
        "team-1",
        "device-1");

    [Fact]
    public async Task SaveReadAndDeleteUseOpaqueCredentialTarget()
    {
        const string token = "secret-cloud-access-token";
        var credentials = new RecordingCredentialStore();
        ICloudAccessTokenVault provider = new CredentialCloudAccessTokenProvider(
            credentials,
            Profile);

        provider.SaveAccessToken(token);
        var restored = await provider.GetAccessTokenAsync(CancellationToken.None);
        var deleted = provider.DeleteAccessToken();

        Assert.Equal(token, restored);
        Assert.True(deleted);
        Assert.Equal(Profile.AccessTokenCredentialName, credentials.LastName);
        Assert.DoesNotContain(token, provider.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCredentialFailsWithoutExposingTargetOrTokenMaterial()
    {
        var credentials = new RecordingCredentialStore();
        var provider = new CredentialCloudAccessTokenProvider(credentials, Profile);

        var exception = await Assert.ThrowsAsync<CloudAccessTokenStoreException>(
            () => provider.GetAccessTokenAsync(CancellationToken.None).AsTask());

        Assert.DoesNotContain(
            Profile.AccessTokenCredentialName!,
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void UnderlyingFailureNeverEchoesAccessToken()
    {
        const string token = "must-never-appear-in-an-error";
        var credentials = new RecordingCredentialStore
        {
            SaveException = new InvalidOperationException(token),
        };
        var provider = new CredentialCloudAccessTokenProvider(credentials, Profile);

        var exception = Assert.Throws<CloudAccessTokenStoreException>(
            () => provider.SaveAccessToken(token));

        Assert.DoesNotContain(token, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task CancellationIsObservedBeforeCredentialManagerAccess()
    {
        var credentials = new RecordingCredentialStore();
        var provider = new CredentialCloudAccessTokenProvider(credentials, Profile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetAccessTokenAsync(cancellation.Token).AsTask());

        Assert.Equal(0, credentials.ReadCount);
    }

    [Fact]
    public void LocalOnlyProfileCannotCreateRemoteCredentialProvider()
    {
        Assert.Throws<ArgumentException>(
            () => new CredentialCloudAccessTokenProvider(
                new RecordingCredentialStore(),
                new CloudConnectionProfile()));
    }

    private sealed class RecordingCredentialStore : ICredentialStore
    {
        private string? _value;

        public string? LastName { get; private set; }

        public int ReadCount { get; private set; }

        public Exception? SaveException { get; init; }

        public void Save(string name, string secret)
        {
            LastName = name;
            if (SaveException is not null)
            {
                throw SaveException;
            }
            _value = secret;
        }

        public string? Read(string name)
        {
            LastName = name;
            ReadCount++;
            return _value;
        }

        public bool Delete(string name)
        {
            LastName = name;
            _value = null;
            return true;
        }
    }
}
