using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud.Tests;

public sealed class CloudDatabasePathTests : IDisposable
{
    private const string BootstrapToken = "agentdesk-path-test-bootstrap-token-0000000000";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-cloud-path-{Guid.NewGuid():N}");
    private readonly List<string> _directoryLinks = [];

    [Fact]
    public void RelativeDatabasePathFailsAtStartup()
    {
        Directory.CreateDirectory(_root);
        var relativePath = $"agentdesk-relative-{Guid.NewGuid():N}.db";
        using var factory = CreateFactory(relativePath);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("fully qualified local path", error.Message, StringComparison.Ordinal);
        DeleteDatabaseFamily(Path.GetFullPath(relativePath));
    }

    [Fact]
    public async Task FreshDatabaseDirectoriesCanBeCreatedUnderASafeExistingAncestor()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "new", "nested", "cloud.db");
        await using var factory = CreateFactory(database);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, health.StatusCode);
        Assert.True(File.Exists(database));
    }

    [Fact]
    public void DatabasePathThroughReparseDirectoryFailsAtStartup()
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        CreateDirectoryLink(link, target);
        _directoryLinks.Add(link);
        using var factory = CreateFactory(Path.Combine(link, "cloud.db"));

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DatabaseFileWithMultipleHardLinksFailsAtStartup()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.db");
        var alias = Path.Combine(_root, "alias.db");
        File.WriteAllBytes(source, "SQLite format 3\0"u8.ToArray());
        CreateHardLink(alias, source);
        using var factory = CreateFactory(alias);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateClient());

        Assert.Contains("hard link", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreexistingHardLinkedServiceLockFailsWithoutTruncatingItsTarget()
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "cloud.db");
        var sentinel = Path.Combine(_root, "sentinel.txt");
        var sentinelBytes = "must-not-be-truncated"u8.ToArray();
        File.WriteAllBytes(sentinel, sentinelBytes);
        CreateHardLink(database + ".service.lock", sentinel);
        using var factory = CreateFactory(database);

        _ = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Equal(sentinelBytes, File.ReadAllBytes(sentinel));
    }

    public void Dispose()
    {
        foreach (var link in _directoryLinks)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(link);
            }
            else
            {
                File.Delete(link);
            }
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string databasePath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.ConfigureAppConfiguration(
                (_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AgentDeskCloud:BootstrapToken"] = BootstrapToken,
                        ["AgentDeskCloud:DatabasePath"] = databasePath,
                        ["AgentDeskCloud:RequireHttps"] = "false",
                        ["AgentDeskCloud:AutomationPollingIntervalSeconds"] = "300",
                    })));

    private static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "/d", "/c", "mklink", "/J", link, target },
                })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            return;
        }

        Directory.CreateSymbolicLink(link, target);
    }

    private static void CreateHardLink(string link, string target)
    {
        var executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/ln";
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/H");
            startInfo.ArgumentList.Add(link);
            startInfo.ArgumentList.Add(target);
        }
        else
        {
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add(link);
        }
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private static void DeleteDatabaseFamily(string databasePath)
    {
        foreach (var path in new[]
                 {
                     databasePath,
                     databasePath + "-wal",
                     databasePath + "-shm",
                     databasePath + ".service.lock",
                 })
        {
            File.Delete(path);
        }
    }
}
