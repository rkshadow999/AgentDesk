using AgentDesk.App.Maintenance;
using AgentDesk.Core.Engine;

namespace AgentDesk.App.Tests;

public sealed class SessionDocumentFileStoreTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsTheValidatedDocument()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.agentdesk-session.json");
        var source = EngineSessionDocument.FromJson(
            """
            {"schemaVersion":1,"session":{"id":"session-42"}}
            """);

        var store = new SessionDocumentFileStore();
        await store.SaveAsync(path, source);
        var loaded = await store.LoadAsync(path);

        Assert.Equal(source.ExportUtf8Json(), loaded.ExportUtf8Json());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task SaveAsync_ReplacesARegularFileAtomically()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "session.agentdesk-session.json");
        await File.WriteAllTextAsync(path, "old");
        var document = EngineSessionDocument.FromJson("{\"schemaVersion\":1}");

        await new SessionDocumentFileStore().SaveAsync(path, document);

        Assert.Equal(document.ExportUtf8Json(), await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task LoadAsync_RejectsAnOversizedDocumentBeforeParsing()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "oversized.agentdesk-session.json");
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(EngineSessionDocument.MaximumBytes + 1L);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SessionDocumentFileStore().LoadAsync(path));
    }

    [Fact]
    public async Task LoadAsync_RejectsInvalidJson()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "invalid.agentdesk-session.json");
        await File.WriteAllTextAsync(path, "not-json");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new SessionDocumentFileStore().LoadAsync(path));
    }

    [Fact]
    public async Task SaveAndLoadAsync_RejectReparsePointsWhenSupported()
    {
        using var directory = new TemporaryDirectory();
        var target = Path.Combine(directory.Path, "target.agentdesk-session.json");
        var link = Path.Combine(directory.Path, "link.agentdesk-session.json");
        await File.WriteAllTextAsync(target, "{\"schemaVersion\":1}");

        try
        {
            _ = File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var store = new SessionDocumentFileStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(link));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(link, EngineSessionDocument.FromJson("{\"schemaVersion\":1}")));
    }

    [Fact]
    public async Task SaveAsync_RejectsAlternateDataStreamsAndReservedDeviceNames()
    {
        using var directory = new TemporaryDirectory();
        var document = EngineSessionDocument.FromJson("{\"schemaVersion\":1}");
        var store = new SessionDocumentFileStore();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(
                Path.Combine(directory.Path, "session.agentdesk-session.json:payload"),
                document));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(
                Path.Combine(directory.Path, "CON.agentdesk-session.json"),
                document));
    }

    [Fact]
    public async Task SaveAndLoadAsync_RejectAReparsePointAncestorWhenSupported()
    {
        using var directory = new TemporaryDirectory();
        var targetDirectory = Path.Combine(directory.Path, "target");
        var linkDirectory = Path.Combine(directory.Path, "linked");
        Directory.CreateDirectory(targetDirectory);
        try
        {
            _ = Directory.CreateSymbolicLink(linkDirectory, targetDirectory);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var path = Path.Combine(linkDirectory, "session.agentdesk-session.json");
        var store = new SessionDocumentFileStore();
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(path, EngineSessionDocument.FromJson("{\"schemaVersion\":1}")));

        await File.WriteAllTextAsync(
            Path.Combine(targetDirectory, "session.agentdesk-session.json"),
            "{\"schemaVersion\":1}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
    }

    [Fact]
    public async Task SaveAndLoadAsync_RejectUncPathsBeforeFileSystemAccess()
    {
        const string path = @"\\localhost\agentdesk-missing-share\session.agentdesk-session.json";
        var store = new SessionDocumentFileStore();
        var document = EngineSessionDocument.FromJson("{\"schemaVersion\":1}");

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(path, document));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agentdesk-session-file-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
