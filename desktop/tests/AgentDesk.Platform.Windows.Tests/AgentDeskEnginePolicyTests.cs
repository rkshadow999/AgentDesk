using AgentDesk.Platform.Windows.Settings;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class AgentDeskEnginePolicyTests
{
    [Fact]
    public async Task EnsureAsyncPinsRemoteFetchOffAndReplacesUnsafeExistingPolicy()
    {
        var directory = Directory.CreateTempSubdirectory("agentdesk-policy-");
        try
        {
            var path = Path.Combine(directory.FullName, "requirements.toml");
            await File.WriteAllTextAsync(path, "[features]\nremote_fetch = true\n");

            await AgentDeskEnginePolicy.EnsureAsync(directory.FullName);

            Assert.Equal(
                "[features]\nremote_fetch = false\n",
                await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory.FullName, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
