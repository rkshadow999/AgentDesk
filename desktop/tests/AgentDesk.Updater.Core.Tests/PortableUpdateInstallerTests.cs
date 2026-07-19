using System.Text.Json;
using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class PortableUpdateInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"AgentDesk.Installer.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task InstallWaitsForTheParentThenAtomicallyActivatesAndRestarts()
    {
        var fixture = CreateFixture();
        var events = new List<string>();
        var process = new RecordingProcessController(events);
        var directories = new RecordingDirectoryOperations(events);
        var installer = new PortableUpdateInstaller(directories, process);
        var arguments = new[] { "--message", "hello world & calc.exe", "中文" };

        var result = await installer.InstallAsync(new PortableInstallRequest(
            fixture.Installation,
            fixture.State,
            fixture.Payload,
            SemanticVersion.Parse("2.0.0"),
            "AgentDesk.exe",
            ParentProcessId: 42,
            ParentExitTimeout: TimeSpan.FromSeconds(5),
            arguments));

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "AgentDesk.exe")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(result.BackupDirectory, "AgentDesk.exe")));
        Assert.Equal("wait:42", events[0]);
        Assert.StartsWith("move:", events[1], StringComparison.Ordinal);
        Assert.Equal(Path.Combine(fixture.Installation, "AgentDesk.exe"), process.ExecutablePath);
        Assert.Equal(arguments, process.Arguments);
        Assert.False(File.Exists(Path.Combine(fixture.State, "transaction.json")));
    }

    [Fact]
    public async Task InstallRestoresTheOldVersionWhenRestartFails()
    {
        var fixture = CreateFixture();
        var process = new RecordingProcessController([]) { StartException = new InvalidOperationException("start failed") };
        var installer = new PortableUpdateInstaller(new RecordingDirectoryOperations([]), process);

        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(Request(fixture)));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "AgentDesk.exe")));
        Assert.False(File.Exists(Path.Combine(fixture.State, "transaction.json")));
    }

    [Fact]
    public async Task InstallRestoresTheOldVersionWhenActivationMoveFails()
    {
        var fixture = CreateFixture();
        var directories = new RecordingDirectoryOperations([]) { FailMoveNumber = 2 };
        var installer = new PortableUpdateInstaller(directories, new RecordingProcessController([]));

        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(Request(fixture)));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "AgentDesk.exe")));
        Assert.True(Directory.Exists(fixture.Payload));
        Assert.False(File.Exists(Path.Combine(fixture.State, "transaction.json")));
    }

    [Fact]
    public async Task InstallDoesNotTouchFilesWhenTheParentDoesNotExit()
    {
        var fixture = CreateFixture();
        var process = new RecordingProcessController([]) { WaitException = new TimeoutException("still running") };
        var directories = new RecordingDirectoryOperations([]);
        var installer = new PortableUpdateInstaller(directories, process);

        var request = Request(fixture) with { ParentProcessId = 42 };

        await Assert.ThrowsAsync<TimeoutException>(() => installer.InstallAsync(request));

        Assert.Empty(directories.Moves);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "AgentDesk.exe")));
    }

    [Fact]
    public async Task RecoverConservativelyRollsBackAnInterruptedActivatedTransaction()
    {
        var fixture = CreateFixture();
        var backup = Path.Combine(fixture.State, "backups", "tx-1");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.Move(fixture.Installation, backup);
        Directory.Move(fixture.Payload, fixture.Installation);
        await WriteJournalAsync(
            fixture,
            backup,
            transactionId: "0123456789abcdef0123456789abcdef",
            phase: "activated");
        var installer = new PortableUpdateInstaller(
            new RecordingDirectoryOperations([]),
            new RecordingProcessController([]));

        var recovered = await installer.RecoverAsync(fixture.Installation, fixture.State);

        Assert.True(recovered);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "AgentDesk.exe")));
        Assert.False(File.Exists(Path.Combine(fixture.State, "transaction.json")));
    }

    [Fact]
    public async Task RecoverRejectsAJournalThatPointsOutsideTheUpdateStateRoot()
    {
        var fixture = CreateFixture();
        await WriteJournalAsync(
            fixture,
            Path.Combine(_directory, "attacker-controlled"),
            transactionId: "0123456789abcdef0123456789abcdef",
            phase: "backupCreated");
        var installer = new PortableUpdateInstaller(
            new RecordingDirectoryOperations([]),
            new RecordingProcessController([]));

        await Assert.ThrowsAsync<UpdateSecurityException>(
            () => installer.RecoverAsync(fixture.Installation, fixture.State));

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(fixture.Installation, "AgentDesk.exe")));
    }

    [Theory]
    [InlineData("bad\0argument")]
    [InlineData("bad\nargument")]
    public async Task InstallRejectsUnsafeRestartArgumentsBeforeWaiting(string argument)
    {
        var fixture = CreateFixture();
        var process = new RecordingProcessController([]);
        var request = Request(fixture) with { RestartArguments = [argument] };

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            new PortableUpdateInstaller(new RecordingDirectoryOperations([]), process).InstallAsync(request));

        Assert.False(process.WaitCalled);
    }

    [Fact]
    public async Task InstallRejectsAnUpdateStateDirectoryEqualToTheInstallationDirectory()
    {
        var installation = Path.Combine(_directory, "AgentDesk");
        var payload = Path.Combine(installation, "staging", "tx", "payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(installation, "AgentDesk.exe"), "old");
        File.WriteAllText(Path.Combine(payload, "AgentDesk.exe"), "new");
        var process = new RecordingProcessController([]);
        var request = new PortableInstallRequest(
            installation,
            installation,
            payload,
            SemanticVersion.Parse("2.0.0"),
            "AgentDesk.exe",
            ParentProcessId: null,
            ParentExitTimeout: TimeSpan.FromSeconds(5),
            RestartArguments: []);

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            new PortableUpdateInstaller(new RecordingDirectoryOperations([]), process)
                .InstallAsync(request));

        Assert.False(process.WaitCalled);
    }

    private Fixture CreateFixture()
    {
        var installation = Path.Combine(_directory, "AgentDesk");
        var state = Path.Combine(_directory, ".agentdesk-update");
        var payload = Path.Combine(state, "staging", "tx", "payload");
        Directory.CreateDirectory(installation);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(installation, "AgentDesk.exe"), "old");
        File.WriteAllText(Path.Combine(payload, "AgentDesk.exe"), "new");
        return new Fixture(installation, state, payload);
    }

    private static PortableInstallRequest Request(Fixture fixture) => new(
        fixture.Installation,
        fixture.State,
        fixture.Payload,
        SemanticVersion.Parse("2.0.0"),
        "AgentDesk.exe",
        ParentProcessId: null,
        ParentExitTimeout: TimeSpan.FromSeconds(5),
        RestartArguments: []);

    private static async Task WriteJournalAsync(
        Fixture fixture,
        string backup,
        string transactionId,
        string phase)
    {
        Directory.CreateDirectory(fixture.State);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            transactionId,
            installationDirectory = Path.GetFullPath(fixture.Installation),
            stagedPayloadDirectory = Path.GetFullPath(fixture.Payload),
            backupDirectory = Path.GetFullPath(backup),
            entryPoint = "AgentDesk.exe",
            phase,
        });
        await File.WriteAllTextAsync(Path.Combine(fixture.State, "transaction.json"), json);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record Fixture(string Installation, string State, string Payload);

    private sealed class RecordingProcessController(ICollection<string> events)
        : IUpdateProcessController
    {
        public Exception? WaitException { get; init; }

        public Exception? StartException { get; init; }

        public bool WaitCalled { get; private set; }

        public string? ExecutablePath { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task WaitForExitAsync(
            int processId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WaitCalled = true;
            events.Add($"wait:{processId}");
            return WaitException is null ? Task.CompletedTask : Task.FromException(WaitException);
        }

        public void Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments)
        {
            events.Add("start");
            ExecutablePath = executablePath;
            Arguments = arguments.ToArray();
            if (StartException is not null)
            {
                throw StartException;
            }
        }
    }

    private sealed class RecordingDirectoryOperations(ICollection<string> events)
        : IUpdateDirectoryOperations
    {
        private int _moveCount;

        public int? FailMoveNumber { get; init; }

        public List<(string Source, string Destination)> Moves { get; } = [];

        public bool Exists(string path) => Directory.Exists(path);

        public void Create(string path) => Directory.CreateDirectory(path);

        public void Move(string source, string destination)
        {
            _moveCount++;
            if (_moveCount == FailMoveNumber)
            {
                throw new IOException("Injected move failure.");
            }

            events.Add($"move:{source}->{destination}");
            Moves.Add((source, destination));
            Directory.Move(source, destination);
        }
    }
}
