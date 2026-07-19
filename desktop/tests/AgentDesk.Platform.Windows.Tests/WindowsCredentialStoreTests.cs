using System.Text;
using AgentDesk.Platform.Windows.Credentials;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void SavePrefixesTheTargetAndClearsTheTemporarySecretBuffer()
    {
        var api = new RecordingCredentialApi();
        var store = new WindowsCredentialStore(api, "AgentDesk.Tests");

        store.Save("xai.api_key", "xai-secret");

        Assert.Equal("AgentDesk.Tests/xai.api_key", api.WrittenTarget);
        Assert.Equal("AgentDesk", api.WrittenUserName);
        Assert.NotNull(api.WrittenSecret);
        Assert.All(api.WrittenSecret!, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReadDecodesUtf8AndClearsTheNativeSecretBuffer()
    {
        var nativeSecret = Encoding.UTF8.GetBytes("中文密钥");
        var api = new RecordingCredentialApi { SecretToRead = nativeSecret };
        var store = new WindowsCredentialStore(api, "AgentDesk.Tests");

        var secret = store.Read("xai.api_key");

        Assert.Equal("中文密钥", secret);
        Assert.Equal("AgentDesk.Tests/xai.api_key", api.ReadTarget);
        Assert.All(nativeSecret, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReadReturnsNullWhenTheCredentialDoesNotExist()
    {
        var store = new WindowsCredentialStore(new RecordingCredentialApi(), "AgentDesk.Tests");

        Assert.Null(store.Read("xai.api_key"));
    }

    [Fact]
    public void DeleteUsesTheNamespacedTarget()
    {
        var api = new RecordingCredentialApi { DeleteResult = true };
        var store = new WindowsCredentialStore(api, "AgentDesk.Tests");

        Assert.True(store.Delete("xai.api_key"));
        Assert.Equal("AgentDesk.Tests/xai.api_key", api.DeletedTarget);
    }

    private sealed class RecordingCredentialApi : ICredentialApi
    {
        public string? WrittenTarget { get; private set; }
        public string? WrittenUserName { get; private set; }
        public byte[]? WrittenSecret { get; private set; }
        public string? ReadTarget { get; private set; }
        public byte[]? SecretToRead { get; init; }
        public string? DeletedTarget { get; private set; }
        public bool DeleteResult { get; init; }

        public void Write(string target, string userName, byte[] secret)
        {
            WrittenTarget = target;
            WrittenUserName = userName;
            WrittenSecret = secret;
        }

        public byte[]? Read(string target)
        {
            ReadTarget = target;
            return SecretToRead;
        }

        public bool Delete(string target)
        {
            DeletedTarget = target;
            return DeleteResult;
        }
    }
}
