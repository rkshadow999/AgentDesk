using AgentDesk.App.Cloud;

namespace AgentDesk.App.Tests;

public sealed class PairingPackageFileStoreTests
{
    [Fact]
    public async Task WriteAndReadRoundTripUsesTheDedicatedPackageExtension()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "recovery.agentdesk-pairing");
            var store = new PairingPackageFileStore(maximumBytes: 16);

            await store.WriteAsync(path, new byte[] { 1, 2, 3, 4 });
            Assert.True(
                File.Exists(path),
                $"Expected target after write. Entries: {string.Join(", ", Directory.EnumerateFileSystemEntries(directory))}");
            var result = await store.ReadAsync(path);

            Assert.Equal([1, 2, 3, 4], result);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledWritePreservesTheExistingPackage()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "recovery.agentdesk-pairing");
            await File.WriteAllBytesAsync(path, [9, 9, 9]);
            var store = new PairingPackageFileStore(maximumBytes: 16);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.WriteAsync(path, new byte[] { 1, 2, 3 }, cancellation.Token));

            Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAtomicallyReplacesAnExistingPackage()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "recovery.agentdesk-pairing");
            await File.WriteAllBytesAsync(path, [9, 9, 9]);
            var store = new PairingPackageFileStore(maximumBytes: 16);

            await store.WriteAsync(path, new byte[] { 1, 2, 3, 4 });

            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(path));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("recovery.bin")]
    [InlineData("recovery.agentdesk-pairing:stream")]
    public async Task RejectsWrongExtensionsAndAlternateDataStreams(string fileName)
    {
        var directory = CreateDirectory();
        try
        {
            var store = new PairingPackageFileStore(maximumBytes: 16);
            var path = Path.Combine(directory, fileName);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.WriteAsync(path, new byte[] { 1, 2, 3 }));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsPackagesOutsideTheConfiguredSizeLimit()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "recovery.agentdesk-pairing");
            var store = new PairingPackageFileStore(maximumBytes: 4);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.WriteAsync(path, new byte[] { 1, 2, 3, 4, 5 }));
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("CON.agentdesk-pairing")]
    [InlineData("NUL.agentdesk-pairing")]
    [InlineData("COM1.agentdesk-pairing")]
    [InlineData("LPT9.agentdesk-pairing")]
    public async Task RejectsWindowsReservedDeviceNames(string fileName)
    {
        var directory = CreateDirectory();
        try
        {
            var store = new PairingPackageFileStore(maximumBytes: 16);
            var path = Path.Combine(directory, fileName);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.WriteAsync(path, new byte[] { 1, 2, 3 }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsWindowsDeviceNamespacePaths()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new PairingPackageFileStore(maximumBytes: 16);
            var extendedPath = $@"\\?\{Path.Combine(directory, "recovery.agentdesk-pairing")}";

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.WriteAsync(extendedPath, new byte[] { 1, 2, 3 }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsAReparsePointPackageWithoutReadingItsTarget()
    {
        var directory = CreateDirectory();
        var targetDirectory = CreateDirectory();
        try
        {
            var target = Path.Combine(targetDirectory, "target.agentdesk-pairing");
            await File.WriteAllBytesAsync(target, [7, 7, 7]);
            var link = Path.Combine(directory, "recovery.agentdesk-pairing");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException)
            {
                return;
            }

            var store = new PairingPackageFileStore(maximumBytes: 16);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(link));
            Assert.Equal([7, 7, 7], await File.ReadAllBytesAsync(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            Directory.Delete(targetDirectory, recursive: true);
        }
    }

    [Fact]
    public void DirectoryPathGuardPreventsAncestorReplacementUntilDisposed()
    {
        var root = CreateDirectory();
        var parent = Path.Combine(root, "parent");
        var leaf = Path.Combine(parent, "leaf");
        var moved = Path.Combine(root, "moved");
        Directory.CreateDirectory(leaf);
        try
        {
            using (PairingPackageFileStore.DirectoryPathGuard.Acquire(leaf))
            {
                var blocked = Record.Exception(() => Directory.Move(parent, moved));
                Assert.True(
                    blocked is IOException or UnauthorizedAccessException,
                    $"Expected the ancestor move to be blocked, got: {blocked}");
            }

            Directory.Move(parent, moved);
            Assert.True(Directory.Exists(Path.Combine(moved, "leaf")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"AgentDesk-pairing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
