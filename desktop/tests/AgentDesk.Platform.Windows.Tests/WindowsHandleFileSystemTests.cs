using AgentDesk.Platform.Windows.IO;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class WindowsHandleFileSystemTests
{
    [Fact]
    public void FileIdentityIncludesAll128FileIdBits()
    {
        var first = new WindowsHandleFileSystem.FileIdentity(
            0x0102030405060708,
            0x1112131415161718,
            0x2122232425262728);
        var differentHighBits = new WindowsHandleFileSystem.FileIdentity(
            0x0102030405060708,
            0x1112131415161718,
            0x3132333435363738);

        Assert.NotEqual(first, differentHighBits);
    }

    [Fact]
    public void DirectoryPathGuardPreventsAncestorReplacementUntilDisposed()
    {
        var root = NewTemporaryDirectory();
        var parent = Path.Combine(root, "parent");
        var leaf = Path.Combine(parent, "leaf");
        var moved = Path.Combine(root, "moved");
        Directory.CreateDirectory(leaf);
        try
        {
            using (WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                       leaf,
                       createIfMissing: false))
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

    [Fact]
    public async Task ReplacementTargetHandlePreventsSubstitutionUntilDisposed()
    {
        var root = NewTemporaryDirectory();
        var target = Path.Combine(root, "target.json");
        var moved = Path.Combine(root, "moved.json");
        await File.WriteAllTextAsync(target, "old");
        try
        {
            using (WindowsHandleFileSystem.OpenExistingReplacementTarget(target))
            {
                var blocked = Record.Exception(() => File.Move(target, moved));
                Assert.True(
                    blocked is IOException or UnauthorizedAccessException,
                    $"Expected the target move to be blocked, got: {blocked}");
            }

            File.Move(target, moved);
            Assert.Equal("old", await File.ReadAllTextAsync(moved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReplacementDirectoryHandlePreventsSubstitutionUntilDisposed()
    {
        var root = NewTemporaryDirectory();
        var target = Path.Combine(root, "target");
        var moved = Path.Combine(root, "moved");
        Directory.CreateDirectory(target);
        try
        {
            using (WindowsHandleFileSystem.OpenExistingDirectoryForReplacement(target))
            {
                var blocked = Record.Exception(() => Directory.Move(target, moved));
                Assert.True(
                    blocked is IOException or UnauthorizedAccessException,
                    $"Expected the directory move to be blocked, got: {blocked}");
            }

            Directory.Move(target, moved);
            Assert.True(Directory.Exists(moved));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicFileReplacementCannotBeHijackedDuringTheRename()
    {
        var root = NewTemporaryDirectory();
        var target = Path.Combine(root, "target.json");
        var replacement = Path.Combine(root, ".replacement.tmp");
        var racedOriginal = Path.Combine(root, "raced-original.json");
        await File.WriteAllTextAsync(target, "old");
        try
        {
            using var guard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                root,
                createIfMissing: false);
            using (var replacementHandle =
                   WindowsHandleFileSystem.CreateTemporaryFile(replacement))
            {
                RandomAccess.Write(replacementHandle, "new"u8, fileOffset: 0);
                RandomAccess.FlushToDisk(replacementHandle);

                WindowsHandleFileSystem.AtomicReplace(
                    replacementHandle,
                    replacement,
                    target,
                    () =>
                    {
                        if (File.Exists(target))
                        {
                            File.Move(target, racedOriginal);
                        }
                        File.WriteAllText(target, "attacker");
                    });
            }

            Assert.Equal("new", await File.ReadAllTextAsync(target));
            Assert.False(File.Exists(replacement));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AtomicDirectoryReplacementQuarantinesARacedTargetName()
    {
        var root = NewTemporaryDirectory();
        var target = Path.Combine(root, "target");
        var replacement = Path.Combine(root, "replacement");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
        await File.WriteAllTextAsync(Path.Combine(replacement, "new.txt"), "new");
        try
        {
            using var parentGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                root,
                createIfMissing: false);
            WindowsHandleFileSystem.FileIdentity replacementIdentity;
            using (var replacementGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                       replacement,
                       createIfMissing: false))
            {
                replacementIdentity = replacementGuard.LeafIdentity;
            }
            using var replacementHandle =
                WindowsHandleFileSystem.OpenExistingDirectoryForReplacement(
                    replacement,
                    replacementIdentity);

            using var displaced = WindowsHandleFileSystem.AtomicReplaceDirectory(
                replacementHandle,
                replacement,
                target,
                () =>
                {
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, "attacker.txt"), "attacker");
                });

            Assert.NotNull(displaced);
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(target, "new.txt")));
            Assert.False(File.Exists(Path.Combine(target, "attacker.txt")));
            WindowsHandleFileSystem.DeleteDirectoryTree(displaced!.Handle, displaced.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedDirectoryReplacementRestoresThePinnedOriginalAfterANameRace()
    {
        var root = NewTemporaryDirectory();
        var target = Path.Combine(root, "target");
        var replacement = Path.Combine(root, "replacement");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(replacement);
        await File.WriteAllTextAsync(Path.Combine(target, "old.txt"), "old");
        var replacementFile = Path.Combine(replacement, "new.txt");
        await File.WriteAllTextAsync(replacementFile, "new");
        FileStream? replacementBlocker = null;
        try
        {
            using var parentGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                root,
                createIfMissing: false);
            WindowsHandleFileSystem.FileIdentity replacementIdentity;
            using (var replacementGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                       replacement,
                       createIfMissing: false))
            {
                replacementIdentity = replacementGuard.LeafIdentity;
            }
            using var replacementHandle =
                WindowsHandleFileSystem.OpenExistingDirectoryForReplacement(
                    replacement,
                    replacementIdentity);

            Assert.Throws<IOException>(() =>
                WindowsHandleFileSystem.AtomicReplaceDirectory(
                    replacementHandle,
                    replacement,
                    target,
                    () =>
                    {
                        Directory.CreateDirectory(target);
                        File.WriteAllText(Path.Combine(target, "attacker.txt"), "attacker");
                        replacementBlocker = File.Open(
                            replacementFile,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read);
                    }));
            replacementBlocker!.Dispose();
            replacementBlocker = null;

            Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(target, "old.txt")));
            Assert.False(File.Exists(Path.Combine(target, "attacker.txt")));
            Assert.Equal("new", await File.ReadAllTextAsync(replacementFile));
        }
        finally
        {
            replacementBlocker?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-handle-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
