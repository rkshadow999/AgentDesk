using System.IO.Compression;
using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class SafeZipExtractorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"AgentDesk.Zip.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExtractAsyncWritesAValidatedPortablePayload()
    {
        var archive = CreateArchive(
            ("AgentDesk.exe", "application"),
            ("resources/zh-CN.pri", "language"));
        var destination = Path.Combine(_directory, "payload");

        await CreateExtractor().ExtractAsync(archive, destination);

        Assert.Equal("application", await File.ReadAllTextAsync(Path.Combine(destination, "AgentDesk.exe")));
        Assert.Equal("language", await File.ReadAllTextAsync(Path.Combine(destination, "resources", "zh-CN.pri")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("folder/file.txt:stream")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/trailing. /file.txt")]
    public async Task ExtractAsyncRejectsWindowsPathTraversalAndAliases(string entryName)
    {
        var archive = CreateArchive((entryName, "payload"));
        var destination = Path.Combine(_directory, "payload");

        await Assert.ThrowsAsync<UpdateSecurityException>(
            () => CreateExtractor().ExtractAsync(archive, destination));

        Assert.False(File.Exists(Path.Combine(_directory, "outside.txt")));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task ExtractAsyncRejectsCaseInsensitiveCollisions()
    {
        var archive = CreateArchive(("Readme.txt", "one"), ("README.TXT", "two"));

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            CreateExtractor().ExtractAsync(archive, Path.Combine(_directory, "payload")));
    }

    [Fact]
    public async Task ExtractAsyncRejectsUnixSymbolicLinks()
    {
        Directory.CreateDirectory(_directory);
        var archive = Path.Combine(_directory, "package.zip");
        await using (var stream = File.Create(archive))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = (0xA000 | 0x1FF) << 16;
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("AgentDesk.exe");
        }

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            CreateExtractor().ExtractAsync(archive, Path.Combine(_directory, "payload")));
    }

    [Fact]
    public async Task ExtractAsyncEnforcesEntryAndTotalUncompressedLimits()
    {
        var archive = CreateArchive(("one.bin", new string('a', 17)));
        var extractor = new SafeZipExtractor(new ZipExtractionLimits(
            MaximumEntries: 10,
            MaximumEntryBytes: 16,
            MaximumTotalBytes: 32));

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            extractor.ExtractAsync(archive, Path.Combine(_directory, "payload")));
    }

    [Fact]
    public async Task ExtractAsyncRejectsDirectoryEntriesThatContainData()
    {
        var archive = CreateArchive(("resources/", "not a directory"));

        await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            CreateExtractor().ExtractAsync(archive, Path.Combine(_directory, "payload")));
    }

    private SafeZipExtractor CreateExtractor() => new(new ZipExtractionLimits(
        MaximumEntries: 100,
        MaximumEntryBytes: 1024,
        MaximumTotalBytes: 4096));

    private string CreateArchive(params (string Name, string Content)[] entries)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
