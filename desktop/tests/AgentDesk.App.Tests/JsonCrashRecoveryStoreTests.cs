using System.Text.Json;
using System.Diagnostics;
using AgentDesk.App.Recovery;
using AgentDesk.Core.Engine;
using AgentDesk.Core.Execution;

namespace AgentDesk.App.Tests;

public sealed class JsonCrashRecoveryStoreTests : IDisposable
{
    private const string ProviderIdentity =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        19,
        3,
        4,
        5,
        TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AgentDesk.CrashRecovery.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsOnlyTheVersionedRecoveryMarker()
    {
        var path = Path.Combine(_directory, "crash-recovery.json");
        var marker = new CrashRecoveryMarker(
            new SessionId("session-42"),
            "C:\\workspace",
            ExecutionProfile.WslStrict,
            SessionMode.Plan,
            Now,
            ProviderIdentity);
        var store = new JsonCrashRecoveryStore(path, new FixedTimeProvider(Now));

        await store.SaveAsync(marker);
        var loaded = await store.LoadAsync();

        Assert.Equal(marker, loaded);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(
            [
                "schemaVersion",
                "sessionId",
                "workspacePath",
                "executionProfile",
                "sessionMode",
                "providerIdentity",
                "updatedAt",
            ],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ProviderIdentity,
            document.RootElement.GetProperty("providerIdentity").GetString());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task LoadAsync_RejectsCorruptJson()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        await File.WriteAllTextAsync(path, "{not-json");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(path).LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_ReportsNonIntegralSchemaVersionsAsInvalidData()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 2.5,
              "sessionId": "session-42",
              "workspacePath": "C:\\workspace",
              "executionProfile": "NativeProtected",
              "sessionMode": "default",
              "providerIdentity": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
              "updatedAt": "2026-07-19T03:04:05.0000000+00:00"
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(path).LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedFilesBeforeParsing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(JsonCrashRecoveryStore.MaximumFileBytes + 1L);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(path).LoadAsync());
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("C:\\workspace\\..\\outside")]
    public async Task LoadAsync_RejectsRelativeAndTraversingWorkspacePaths(string workspacePath)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                sessionId = "session-42",
                workspacePath,
                executionProfile = "NativeProtected",
                sessionMode = "default",
                providerIdentity = ProviderIdentity,
                updatedAt = "2026-07-19T03:04:05.0000000+00:00",
            }));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(path).LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsupportedExecutionProfiles()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 2,
              "sessionId": "session-42",
              "workspacePath": "C:\\workspace",
              "executionProfile": "Unrestricted",
              "sessionMode": "default",
              "providerIdentity": "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
              "updatedAt": "2026-07-19T03:04:05.0000000+00:00"
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(path).LoadAsync());
    }

    [Fact]
    public async Task ClearAsync_RemovesTheMarkerAndLoadReturnsEmpty()
    {
        var path = Path.Combine(_directory, "crash-recovery.json");
        var store = new JsonCrashRecoveryStore(path, new FixedTimeProvider(Now));
        await store.SaveAsync(new CrashRecoveryMarker(
            new SessionId("session-42"),
            "C:\\workspace",
            ExecutionProfile.NativeProtected,
            SessionMode.Default,
            Now,
            ProviderIdentity));

        await store.ClearAsync();

        Assert.False(File.Exists(path));
        Assert.Null(await store.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RejectsSchemaOneMarkersWithoutProviderIdentity()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "sessionId": "session-42",
              "workspacePath": "C:\\workspace",
              "executionProfile": "NativeProtected",
              "sessionMode": "default",
              "updatedAt": "2026-07-19T03:04:05.0000000+00:00"
            }
            """);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(
                path,
                new FixedTimeProvider(Now)).LoadAsync());
    }

    [Theory]
    [InlineData(0, 5, 1)]
    [InlineData(-30, 0, -1)]
    public async Task LoadAsync_RejectsMarkersOutsideTheRecoveryAgeWindow(
        int days,
        int minutes,
        int seconds)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        var updatedAt = Now.AddDays(days).AddMinutes(minutes).AddSeconds(seconds);
        await WriteMarkerAsync(path, updatedAt);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new JsonCrashRecoveryStore(
                path,
                new FixedTimeProvider(Now)).LoadAsync());
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-30, 0)]
    public async Task LoadAsync_AcceptsMarkersAtTheRecoveryAgeBoundaries(
        int days,
        int minutes)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "crash-recovery.json");
        var updatedAt = Now.AddDays(days).AddMinutes(minutes);
        await WriteMarkerAsync(path, updatedAt);

        var marker = await new JsonCrashRecoveryStore(
            path,
            new FixedTimeProvider(Now)).LoadAsync();

        Assert.Equal(updatedAt, Assert.IsType<CrashRecoveryMarker>(marker).UpdatedAt);
    }

    [Fact]
    public async Task SaveLoadAndClearAsync_RejectAReparsePointAncestorWhenSupported()
    {
        var targetDirectory = Path.Combine(_directory, "target");
        var linkedDirectory = Path.Combine(_directory, "linked");
        Directory.CreateDirectory(targetDirectory);
        if (!TryCreateDirectoryJunction(linkedDirectory, targetDirectory))
        {
            return;
        }

        var targetPath = Path.Combine(targetDirectory, "crash-recovery.json");
        var linkedPath = Path.Combine(linkedDirectory, "crash-recovery.json");
        var safeStore = new JsonCrashRecoveryStore(
            targetPath,
            new FixedTimeProvider(Now));
        await safeStore.SaveAsync(CreateMarker());
        var linkedStore = new JsonCrashRecoveryStore(
            linkedPath,
            new FixedTimeProvider(Now));

        try
        {
            await AssertReparseRejectedAsync(() => linkedStore.LoadAsync());
            await AssertReparseRejectedAsync(() => linkedStore.ClearAsync());
            await AssertReparseRejectedAsync(() => linkedStore.SaveAsync(CreateMarker()));
            Assert.True(File.Exists(targetPath));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public async Task SaveLoadAndClearAsync_RejectAReparsePointMarkerWhenSupported()
    {
        Directory.CreateDirectory(_directory);
        var targetPath = Path.Combine(_directory, "target");
        var linkedPath = Path.Combine(_directory, "crash-recovery.json");
        Directory.CreateDirectory(targetPath);
        var sentinel = Path.Combine(targetPath, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        if (!TryCreateDirectoryJunction(linkedPath, targetPath))
        {
            return;
        }

        var linkedStore = new JsonCrashRecoveryStore(
            linkedPath,
            new FixedTimeProvider(Now));

        try
        {
            await AssertReparseRejectedAsync(() => linkedStore.LoadAsync());
            await AssertReparseRejectedAsync(() => linkedStore.ClearAsync());
            await AssertReparseRejectedAsync(() => linkedStore.SaveAsync(CreateMarker()));
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(linkedPath);
        }
    }

    private static async Task AssertReparseRejectedAsync(Func<Task> operation)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(operation);
        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        using var process = Process.Start(new ProcessStartInfo(commandInterpreter)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                linkPath,
                targetPath,
            },
        });
        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(linkPath);
    }

    private static CrashRecoveryMarker CreateMarker() => new(
        new SessionId("session-42"),
        "C:\\workspace",
        ExecutionProfile.NativeProtected,
        SessionMode.Default,
        Now,
        ProviderIdentity);

    private static Task WriteMarkerAsync(string path, DateTimeOffset updatedAt) =>
        File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                sessionId = "session-42",
                workspacePath = "C:\\workspace",
                executionProfile = "NativeProtected",
                sessionMode = "default",
                providerIdentity = ProviderIdentity,
                updatedAt = updatedAt.ToString("O"),
            }));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
