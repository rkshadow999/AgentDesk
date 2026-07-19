using System.IO.Compression;
using AgentDesk.Platform.Windows.Backup;

namespace AgentDesk.Platform.Windows.Tests;

public sealed class AgentDeskBackupServiceTests
{
    [Fact]
    public async Task CreateAndRestoreRoundTripsDesktopAndEngineData()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var restored = Path.Combine(root, "restored");
            var backup = Path.Combine(root, "agentdesk-backup.zip");
            Directory.CreateDirectory(Path.Combine(source, "Engine", "sessions"));
            await File.WriteAllTextAsync(Path.Combine(source, "provider.json"), "{\"model\":\"grok-4.5\"}");
            await File.WriteAllTextAsync(
                Path.Combine(source, "Engine", "sessions", "session.json"),
                "{\"id\":\"session-42\"}");

            var service = new AgentDeskBackupService();
            var created = await service.CreateAsync(source, backup);
            var result = await service.RestoreAsync(backup, restored);

            Assert.Equal(2, created.FileCount);
            Assert.Equal(created.FileCount, result.FileCount);
            Assert.Equal(
                "{\"model\":\"grok-4.5\"}",
                await File.ReadAllTextAsync(Path.Combine(restored, "provider.json")));
            Assert.Equal(
                "{\"id\":\"session-42\"}",
                await File.ReadAllTextAsync(
                    Path.Combine(restored, "Engine", "sessions", "session.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateExcludesWebViewAndUpdateCachesFromTheAuthoritativeBackup()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var restored = Path.Combine(root, "restored");
            var backup = Path.Combine(root, "agentdesk-backup.zip");
            Directory.CreateDirectory(Path.Combine(source, "Engine", "sessions"));
            Directory.CreateDirectory(Path.Combine(source, "WebView2", "Cache"));
            Directory.CreateDirectory(Path.Combine(source, "Updates", "staged"));
            await File.WriteAllTextAsync(
                Path.Combine(source, "Engine", "sessions", "session.json"),
                "{\"id\":\"session-42\"}");
            await File.WriteAllTextAsync(
                Path.Combine(source, "WebView2", "Cache", "browser.cache"),
                "non-authoritative");
            await File.WriteAllTextAsync(
                Path.Combine(source, "Updates", "staged", "AgentDesk.Updater.exe"),
                "non-authoritative");

            var service = new AgentDeskBackupService();
            var created = await service.CreateAsync(source, backup);
            _ = await service.RestoreAsync(backup, restored);

            Assert.Equal(1, created.FileCount);
            Assert.True(File.Exists(
                Path.Combine(restored, "Engine", "sessions", "session.json")));
            Assert.False(Directory.Exists(Path.Combine(restored, "WebView2")));
            Assert.False(Directory.Exists(Path.Combine(restored, "Updates")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateExcludesLiveAttachmentStagingFromTheAuthoritativeBackup()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var restored = Path.Combine(root, "restored");
            var backup = Path.Combine(root, "agentdesk-backup.zip");
            var staging = Path.Combine(
                source,
                "AttachmentStaging",
                "window-0123456789ABCDEF0123456789ABCDEF");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(
                Path.Combine(source, "settings.json"),
                "{\"language\":\"zh-CN\"}");
            await File.WriteAllTextAsync(
                Path.Combine(staging, "PRIVATE.image"),
                "unsent-private-image");
            await using var lease = new FileStream(
                Path.Combine(staging, ".lease"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read);

            var service = new AgentDeskBackupService();
            var created = await service.CreateAsync(source, backup);
            _ = await service.RestoreAsync(backup, restored);

            Assert.Equal(1, created.FileCount);
            Assert.True(File.Exists(Path.Combine(restored, "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(restored, "AttachmentStaging")));
            using var archive = ZipFile.OpenRead(backup);
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.Contains("AttachmentStaging", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsTraversalBeforeChangingExistingData()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var destination = Path.Combine(root, "destination");
            var backup = Path.Combine(root, "malicious.zip");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped.txt");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("escaped");
            }

            var service = new AgentDeskBackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(backup, destination));

            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsTamperedContentBeforeChangingExistingData()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "destination");
            var backup = Path.Combine(root, "backup.zip");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "original");
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            var service = new AgentDeskBackupService();
            _ = await service.CreateAsync(source, backup);

            using (var archive = ZipFile.Open(backup, ZipArchiveMode.Update))
            {
                archive.GetEntry("data/settings.json")!.Delete();
                var replacement = archive.CreateEntry("data/settings.json");
                await using var writer = new StreamWriter(replacement.Open());
                await writer.WriteAsync("tampered");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(backup, destination));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateRejectsDestinationInsideTheSourceTree()
    {
        var root = NewTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(root);
            var service = new AgentDeskBackupService();

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.CreateAsync(root, Path.Combine(root, "backup.zip")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsNullManifestItemsAsInvalidData()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var destination = Path.Combine(root, "destination");
            var backup = Path.Combine(root, "null-manifest.zip");
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            using (var archive = ZipFile.Open(backup, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("manifest.json");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync(
                    "{\"schemaVersion\":1,\"createdAt\":\"2026-07-17T00:00:00Z\",\"files\":[null]}");
            }

            var service = new AgentDeskBackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(backup, destination));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAndRestoreRejectUncPathsBeforeFileSystemAccess()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "destination");
            const string networkFile = @"\\localhost\agentdesk-missing-share\backup.zip";
            Directory.CreateDirectory(source);

            var service = new AgentDeskBackupService();
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.CreateAsync(source, networkFile));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(networkFile, destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateRejectsAReparseSourceRootWhenSupported()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var sourceTarget = Path.Combine(root, "source-target");
            var sourceLink = Path.Combine(root, "source-link");
            var backup = Path.Combine(root, "backup.zip");
            Directory.CreateDirectory(sourceTarget);
            await File.WriteAllTextAsync(Path.Combine(sourceTarget, "settings.json"), "private");
            try
            {
                _ = Directory.CreateSymbolicLink(sourceLink, sourceTarget);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AgentDeskBackupService().CreateAsync(sourceLink, backup));
            Assert.False(File.Exists(backup));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateRejectsAReparseDestinationWithoutChangingItsTargetWhenSupported()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var outside = Path.Combine(root, "outside.zip");
            var destination = Path.Combine(root, "backup.zip");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "private");
            await File.WriteAllTextAsync(outside, "keep");
            try
            {
                _ = File.CreateSymbolicLink(destination, outside);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new AgentDeskBackupService().CreateAsync(source, destination));
            Assert.Equal("keep", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsAReparseBackupFileWhenSupported()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backupTarget = Path.Combine(root, "backup-target.zip");
            var backupLink = Path.Combine(root, "backup-link.zip");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "private");
            var service = new AgentDeskBackupService();
            _ = await service.CreateAsync(source, backupTarget);
            try
            {
                _ = File.CreateSymbolicLink(backupLink, backupTarget);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(backupLink, destination));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsAReparseDestinationRootWithoutChangingItsTargetWhenSupported()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destinationTarget = Path.Combine(root, "destination-target");
            var destinationLink = Path.Combine(root, "destination-link");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destinationTarget);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "new");
            await File.WriteAllTextAsync(Path.Combine(destinationTarget, "keep.txt"), "keep");
            var service = new AgentDeskBackupService();
            _ = await service.CreateAsync(source, backup);
            try
            {
                _ = Directory.CreateSymbolicLink(destinationLink, destinationTarget);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.RestoreAsync(backup, destinationLink));
            Assert.Equal(
                "keep",
                await File.ReadAllTextAsync(Path.Combine(destinationTarget, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAtomicallyReplacesAnExistingDirectory()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "new");
            await File.WriteAllTextAsync(Path.Combine(destination, "obsolete.txt"), "old");
            var service = new AgentDeskBackupService();
            _ = await service.CreateAsync(source, backup);

            _ = await service.RestoreAsync(backup, destination);

            Assert.Equal("new", await File.ReadAllTextAsync(
                Path.Combine(destination, "settings.json")));
            Assert.False(File.Exists(Path.Combine(destination, "obsolete.txt")));
            Assert.Empty(Directory.EnumerateDirectories(root, ".agentdesk-previous-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsVerifiedFileSubstitutionBeforePublication()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(Path.Combine(source, "Engine", "sessions"));
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Combine(source, "Engine", "sessions", "session.json"),
                "trusted");
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            var backupService = new AgentDeskBackupService();
            _ = await backupService.CreateAsync(source, backup);
            var restoreService = new AgentDeskBackupService(
                new AgentDeskBackupService.RestoreValidationTestHooks(
                    BeforePublish: stagingRoot =>
                    {
                        var verifiedFile = Path.Combine(
                            stagingRoot,
                            "Engine",
                            "sessions",
                            "session.json");
                        File.Move(verifiedFile, Path.Combine(stagingRoot, "stolen.json"));
                        File.WriteAllText(verifiedFile, "attacker");
                    }));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                restoreService.RestoreAsync(backup, destination));

            Assert.Equal("keep", await File.ReadAllTextAsync(
                Path.Combine(destination, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(
                destination,
                "Engine",
                "sessions",
                "session.json")));
            Assert.Empty(Directory.EnumerateDirectories(root, ".agentdesk-restore-*"));
            Assert.Empty(Directory.EnumerateDirectories(root, ".agentdesk-previous-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreKeepsPublishedFilePathPinnedThroughValidation()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destination = Path.Combine(root, "destination");
            var stolen = Path.Combine(root, "stolen-after-identity-check.json");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "trusted");
            var backupService = new AgentDeskBackupService();
            _ = await backupService.CreateAsync(source, backup);
            var substitutionBlocked = false;
            var restoreService = new AgentDeskBackupService(
                new AgentDeskBackupService.RestoreValidationTestHooks(
                    AfterPublishedFilePinned: publishedPath =>
                    {
                        var exception = Record.Exception(() =>
                        {
                            File.Move(publishedPath, stolen);
                            File.WriteAllText(publishedPath, "attacker");
                        });
                        substitutionBlocked =
                            exception is IOException or UnauthorizedAccessException;
                    }));

            _ = await restoreService.RestoreAsync(backup, destination);

            Assert.True(substitutionBlocked);
            Assert.Equal("trusted", await File.ReadAllTextAsync(
                Path.Combine(destination, "settings.json")));
            Assert.False(File.Exists(stolen));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsAlternateDataStreamAddedBeforePublication()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "trusted");
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            var backupService = new AgentDeskBackupService();
            _ = await backupService.CreateAsync(source, backup);
            var restoreService = new AgentDeskBackupService(
                new AgentDeskBackupService.RestoreValidationTestHooks(
                    BeforePublish: stagingRoot => File.WriteAllText(
                        Path.Combine(stagingRoot, "settings.json") + ":evil",
                        "attacker")));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                restoreService.RestoreAsync(backup, destination));

            Assert.Equal("keep", await File.ReadAllTextAsync(
                Path.Combine(destination, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRejectsDirectoryEntryAddedAfterEnumeration()
    {
        var root = NewTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "trusted");
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            var backupService = new AgentDeskBackupService();
            _ = await backupService.CreateAsync(source, backup);
            var injected = false;
            var restoreService = new AgentDeskBackupService(
                new AgentDeskBackupService.RestoreValidationTestHooks(
                    AfterDirectoryEnumerated: directoryPath =>
                    {
                        if (!injected && string.Equals(
                                directoryPath,
                                destination,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(Path.Combine(directoryPath, "late.txt"), "attacker");
                            injected = true;
                        }
                    }));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                restoreService.RestoreAsync(backup, destination));

            Assert.True(injected);
            Assert.Equal("keep", await File.ReadAllTextAsync(
                Path.Combine(destination, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRecoversOldTargetWhenKnownStagingNameIsOccupied()
    {
        var root = NewTemporaryDirectory();
        FileStream? stagingBlocker = null;
        try
        {
            var source = Path.Combine(root, "source");
            var backup = Path.Combine(root, "backup.zip");
            var destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(Path.Combine(source, "settings.json"), "trusted");
            await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep");
            var backupService = new AgentDeskBackupService();
            _ = await backupService.CreateAsync(source, backup);
            string? stagingRoot = null;
            var injected = false;
            var restoreService = new AgentDeskBackupService(
                new AgentDeskBackupService.RestoreValidationTestHooks(
                    BeforePublish: path => stagingRoot = path,
                    AfterReplacementMoved: _ =>
                    {
                        Assert.NotNull(stagingRoot);
                        Directory.CreateDirectory(stagingRoot);
                        var blockerPath = Path.Combine(stagingRoot, "blocker.lock");
                        stagingBlocker = File.Open(
                            blockerPath,
                            FileMode.CreateNew,
                            FileAccess.ReadWrite,
                            FileShare.Read);
                    },
                    AfterDirectoryEnumerated: directoryPath =>
                    {
                        if (!injected && string.Equals(
                                directoryPath,
                                destination,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            File.WriteAllText(Path.Combine(directoryPath, "late.txt"), "attacker");
                            injected = true;
                        }
                    }));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                restoreService.RestoreAsync(backup, destination));

            Assert.True(injected);
            Assert.Equal("keep", await File.ReadAllTextAsync(
                Path.Combine(destination, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(destination, "settings.json")));
        }
        finally
        {
            stagingBlocker?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentdesk-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
