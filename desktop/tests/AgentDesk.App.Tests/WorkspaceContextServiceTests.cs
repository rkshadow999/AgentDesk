using AgentDesk.App.Workspace;
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace AgentDesk.App.Tests;

public sealed class WorkspaceContextServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"agentdesk-workspace-context-{Guid.NewGuid():N}");

    [Fact]
    public async Task SearchFilesAsync_ReturnsBoundedRelativeMatchesAndSkipsGeneratedTrees()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "feature"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "package"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "feature", "Parser.cs"), "code");
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "ParserTests.cs"), "tests");
        await File.WriteAllTextAsync(Path.Combine(_root, ".git", "parser-secret"), "ignored");
        await File.WriteAllTextAsync(
            Path.Combine(_root, "node_modules", "package", "parser.js"),
            "ignored");
        var service = new WorkspaceContextService();

        var results = await service.SearchFilesAsync(_root, "parser", limit: 10);

        Assert.Equal(
            ["src/ParserTests.cs", "src/feature/Parser.cs"],
            results.Select(item => item.RelativePath));
        Assert.All(results, item => Assert.InRange(item.ByteLength, 1, 16));
    }

    [Fact]
    public async Task SearchFilesAsync_SkipsFilesWithMultipleHardLinks()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-hardlink-search-{Guid.NewGuid():N}.cs");
        var linked = Path.Combine(_root, "external-result.cs");
        await File.WriteAllTextAsync(outside, "external metadata");
        try
        {
            CreateHardLink(linked, outside);
            var service = new WorkspaceContextService();

            var results = await service.SearchFilesAsync(_root, "external-result");

            Assert.Empty(results);
        }
        finally
        {
            if (File.Exists(linked))
            {
                File.Delete(linked);
            }
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ListInstructionFilesAsync_ReturnsMetadataWithoutReadingFileContents()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "feature"));
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "root instructions");
        await File.WriteAllTextAsync(
            Path.Combine(_root, "src", "AGENTS.md"),
            "source instructions");
        await File.WriteAllTextAsync(
            Path.Combine(_root, "src", "feature", "agents.md"),
            "wrong casing");
        var service = new WorkspaceContextService();

        var results = await service.ListInstructionFilesAsync(_root);

        Assert.Equal(["AGENTS.md", "src/AGENTS.md"], results.Select(item => item.RelativePath));
        Assert.All(results, item => Assert.Equal(TimeSpan.Zero, item.LastWriteTime.Offset));
        Assert.DoesNotContain(
            results,
            item => item.RelativePath.Contains("instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListInstructionFilesAsync_SkipsFilesWithMultipleHardLinks()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "src", "AGENTS.md"),
            "local instructions");
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-hardlink-list-{Guid.NewGuid():N}.md");
        var linked = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(outside, "external instructions");
        try
        {
            CreateHardLink(linked, outside);
            var service = new WorkspaceContextService();

            var results = await service.ListInstructionFilesAsync(_root);

            Assert.Equal(["src/AGENTS.md"], results.Select(item => item.RelativePath));
        }
        finally
        {
            if (File.Exists(linked))
            {
                File.Delete(linked);
            }
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task SearchFilesAsync_RejectsAWorkspaceRootBeneathAReparsePoint()
    {
        var realRoot = Path.Combine(_root, "real");
        var workspace = Path.Combine(realRoot, "workspace");
        var link = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-workspace-search-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "secret.cs"), "outside");
        try
        {
            CreateDirectoryJunction(link, realRoot);

            var service = new WorkspaceContextService();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.SearchFilesAsync(Path.Combine(link, "workspace"), "secret"));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public void EnumerateFiles_PinsANestedDirectoryWhileItsIteratorIsSuspended()
    {
        var nested = Path.Combine(_root, "nested");
        var moved = Path.Combine(_root, "moved");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "one.cs"), "one");
        File.WriteAllText(Path.Combine(nested, "two.cs"), "two");
        var service = new WorkspaceContextService();

        using var enumerator = service
            .EnumerateFiles(_root, CancellationToken.None)
            .GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.StartsWith("nested/", enumerator.Current.RelativePath, StringComparison.Ordinal);

        var error = Record.Exception(() => Directory.Move(nested, moved));
        if (error is null)
        {
            Directory.Move(moved, nested);
        }

        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected the active directory lease to block replacement, got {error?.GetType().Name ?? "no error"}.");
    }

    [Fact]
    public async Task SearchFilesAsync_StopsPullingEntriesAtTheConfiguredBudget()
    {
        Directory.CreateDirectory(_root);
        var first = Path.Combine(_root, "first.cs");
        var second = Path.Combine(_root, "second.cs");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var pulledPastBudget = false;

        IEnumerable<string> EnumerateEntries(string directory)
        {
            Assert.Equal(_root, directory);
            yield return first;
            pulledPastBudget = true;
            yield return second;
        }

        var service = new WorkspaceContextService(1, EnumerateEntries);

        var results = await service.SearchFilesAsync(_root, ".cs", limit: 10);

        Assert.Equal(["first.cs"], results.Select(item => item.RelativePath));
        Assert.False(pulledPastBudget);
    }

    [Fact]
    public async Task ReadTextFileAsync_RejectsTraversalAbsoluteBinaryAndOversizedInputs()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.md"), "hello\nworld");
        await File.WriteAllBytesAsync(Path.Combine(_root, "binary.dat"), [1, 0, 2]);
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "large.txt"),
            new byte[WorkspaceContextService.MaximumReadableFileBytes + 1]);
        var service = new WorkspaceContextService();

        Assert.Equal("hello\nworld", await service.ReadTextFileAsync(_root, "notes.md"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReadTextFileAsync(_root, "../outside.txt"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReadTextFileAsync(_root, Path.GetFullPath("notes.md")));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ReadTextFileAsync(_root, "binary.dat"));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ReadTextFileAsync(_root, "large.txt"));
    }

    [Fact]
    public async Task ReadTextFileAsync_RejectsAFileSymlinkThatLeavesTheWorkspace()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(Path.GetTempPath(), $"agentdesk-outside-{Guid.NewGuid():N}.txt");
        var link = Path.Combine(_root, "linked.txt");
        await File.WriteAllTextAsync(outside, "outside");
        try
        {
            try
            {
                _ = File.CreateSymbolicLink(link, outside);
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException or
                      PlatformNotSupportedException)
            {
                return;
            }

            var service = new WorkspaceContextService();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ReadTextFileAsync(_root, "linked.txt"));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ReadTextFileAsync_RejectsAFileWithMultipleHardLinks()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-hardlink-read-{Guid.NewGuid():N}.txt");
        var linked = Path.Combine(_root, "linked.txt");
        await File.WriteAllTextAsync(outside, "outside secret");
        try
        {
            CreateHardLink(linked, outside);
            var service = new WorkspaceContextService();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ReadTextFileAsync(_root, "linked.txt"));
            Assert.Equal("outside secret", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            if (File.Exists(linked))
            {
                File.Delete(linked);
            }
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ReadAndWriteRejectAWorkspaceRootReparsePoint()
    {
        Directory.CreateDirectory(_root);
        var realWorkspace = Path.Combine(_root, "real");
        var link = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-workspace-link-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realWorkspace);
        await File.WriteAllTextAsync(Path.Combine(realWorkspace, "AGENTS.md"), "outside");
        try
        {
            try
            {
                _ = Directory.CreateSymbolicLink(link, realWorkspace);
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException or IOException or
                      PlatformNotSupportedException)
            {
                return;
            }

            var service = new WorkspaceContextService();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ReadTextFileAsync(link, "AGENTS.md"));
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.WriteInstructionFileAsync(link, "AGENTS.md", "replace"));
            Assert.Equal("outside", await File.ReadAllTextAsync(
                Path.Combine(realWorkspace, "AGENTS.md")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    [Fact]
    public async Task VerifiedFileLeaseBlocksFileAndDirectoryReplacementAfterValidation()
    {
        var sourceDirectory = Path.Combine(_root, "src");
        var sourceFile = Path.Combine(sourceDirectory, "AGENTS.md");
        Directory.CreateDirectory(sourceDirectory);
        await File.WriteAllTextAsync(sourceFile, "rules");

        using (WindowsWorkspaceFileLease.OpenForRead(
                   _root,
                   "src/AGENTS.md",
                   WorkspaceContextService.MaximumReadableFileBytes))
        {
            var fileError = Record.Exception(() => File.Move(
                sourceFile,
                Path.Combine(sourceDirectory, "moved.md")));
            Assert.True(fileError is IOException or UnauthorizedAccessException);

            var directoryError = Record.Exception(() => Directory.Move(
                sourceDirectory,
                Path.Combine(_root, "moved-src")));
            Assert.True(directoryError is IOException or UnauthorizedAccessException);
        }

        Assert.Equal("rules", await File.ReadAllTextAsync(sourceFile));
    }

    [Fact]
    public async Task WriteInstructionFileAsync_AtomicallyUpdatesAnExistingAgentsFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var instructions = Path.Combine(_root, "src", "AGENTS.md");
        var ordinaryFile = Path.Combine(_root, "src", "notes.md");
        await File.WriteAllTextAsync(instructions, "old instructions");
        await File.WriteAllTextAsync(ordinaryFile, "keep");
        var service = new WorkspaceContextService();

        await service.WriteInstructionFileAsync(
            _root,
            "src/AGENTS.md",
            "# Updated\n\nUse tests first.\n");

        Assert.Equal(
            "# Updated\n\nUse tests first.\n",
            await File.ReadAllTextAsync(instructions));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.WriteInstructionFileAsync(_root, "src/notes.md", "replace"));
        Assert.Equal("keep", await File.ReadAllTextAsync(ordinaryFile));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(_root, "src"),
            ".AGENTS.md.agentdesk-*.tmp"));
    }

    [Fact]
    public async Task WriteInstructionFileAsync_CreatesANewAgentsFileWithoutReplacingOtherFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var ordinaryFile = Path.Combine(_root, "src", "notes.md");
        await File.WriteAllTextAsync(ordinaryFile, "keep");
        var service = new WorkspaceContextService();

        await service.WriteInstructionFileAsync(
            _root,
            "src/AGENTS.md",
            "# Workspace rules\n\nUse focused tests.\n");

        Assert.Equal(
            "# Workspace rules\n\nUse focused tests.\n",
            await File.ReadAllTextAsync(Path.Combine(_root, "src", "AGENTS.md")));
        Assert.Equal("keep", await File.ReadAllTextAsync(ordinaryFile));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(_root, "src"),
            ".AGENTS.md.agentdesk-*.tmp"));
    }

    [Fact]
    public async Task CreateInstructionFileAsync_DoesNotExposePlaintextThroughAHardLink()
    {
        Directory.CreateDirectory(_root);
        var instructions = Path.Combine(_root, "AGENTS.md");
        var exposedLink = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-hardlink-create-{Guid.NewGuid():N}.md");
        var content = Encoding.UTF8.GetBytes("new private instructions");
        var hardLinkAttempted = false;
        var hardLinkCreated = false;
        var hardLinkError = 0;
        try
        {
            using var memory = new PinCallbackMemoryManager(content, () =>
            {
                hardLinkAttempted = true;
                hardLinkCreated = CreateHardLinkNative(
                    exposedLink,
                    instructions,
                    IntPtr.Zero);
                hardLinkError = Marshal.GetLastWin32Error();
            });

            var error = await Record.ExceptionAsync(() =>
                WindowsWorkspaceFileLease.CreateInstructionFileAsync(
                    _root,
                    "AGENTS.md",
                    memory.Memory,
                    WorkspaceContextService.MaximumReadableFileBytes,
                    CancellationToken.None));

            Assert.True(hardLinkAttempted);
            Assert.False(
                hardLinkCreated,
                $"The new instruction file was exposed through a hard link (Win32 {hardLinkError}).");
            Assert.Null(error);
            Assert.False(File.Exists(exposedLink));
            Assert.Equal("new private instructions", await File.ReadAllTextAsync(instructions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (File.Exists(exposedLink))
            {
                File.Delete(exposedLink);
            }
        }
    }

    [Fact]
    public async Task CreateInstructionFileAsync_RemovesTheTargetWhenCancelled()
    {
        Directory.CreateDirectory(_root);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var content = Encoding.UTF8.GetBytes("cancelled instructions");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                WindowsWorkspaceFileLease.CreateInstructionFileAsync(
                    _root,
                    "AGENTS.md",
                    content,
                    WorkspaceContextService.MaximumReadableFileBytes,
                    cancellation.Token));

            Assert.False(File.Exists(Path.Combine(_root, "AGENTS.md")));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    [Fact]
    public async Task WriteInstructionFileAsync_RejectsANestedJunctionWhenCreatingAgents()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-junction-create-{Guid.NewGuid():N}");
        var linkedDirectory = Path.Combine(_root, "linked");
        Directory.CreateDirectory(outside);
        try
        {
            CreateDirectoryJunction(linkedDirectory, outside);
            var service = new WorkspaceContextService();

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.WriteInstructionFileAsync(
                    _root,
                    "linked/AGENTS.md",
                    "outside instructions"));

            Assert.False(File.Exists(Path.Combine(outside, "AGENTS.md")));
        }
        finally
        {
            if (Directory.Exists(linkedDirectory))
            {
                Directory.Delete(linkedDirectory);
            }
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task WriteInstructionFileAsync_RejectsAnAgentsFileWithMultipleHardLinks()
    {
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-hardlink-write-{Guid.NewGuid():N}.md");
        var instructions = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(outside, "outside instructions");
        try
        {
            CreateHardLink(instructions, outside);
            var service = new WorkspaceContextService();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.WriteInstructionFileAsync(
                    _root,
                    "AGENTS.md",
                    "replacement instructions"));
            Assert.Equal("outside instructions", await File.ReadAllTextAsync(outside));
            Assert.Equal("outside instructions", await File.ReadAllTextAsync(instructions));
            Assert.Empty(Directory.EnumerateFiles(
                _root,
                ".AGENTS.md.agentdesk-*.tmp"));
        }
        finally
        {
            if (File.Exists(instructions))
            {
                File.Delete(instructions);
            }
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ReplaceAsync_DoesNotExposePlaintextThroughATemporaryHardLink()
    {
        Directory.CreateDirectory(_root);
        var instructions = Path.Combine(_root, "AGENTS.md");
        var exposedLink = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-hardlink-temporary-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(instructions, "original instructions");
        var replacement = Encoding.UTF8.GetBytes("replacement instructions");
        var hardLinkAttempted = false;
        var hardLinkCreated = false;
        var hardLinkError = 0;
        try
        {
            using var lease = WindowsWorkspaceFileLease.OpenForWrite(
                _root,
                "AGENTS.md",
                WorkspaceContextService.MaximumReadableFileBytes);
            using var memory = new PinCallbackMemoryManager(replacement, () =>
            {
                hardLinkAttempted = true;
                var temporaryPath = Assert.Single(Directory.EnumerateFiles(
                    _root,
                    ".AGENTS.md.agentdesk-*.tmp"));
                hardLinkCreated = CreateHardLinkNative(
                    exposedLink,
                    temporaryPath,
                    IntPtr.Zero);
                hardLinkError = Marshal.GetLastWin32Error();
            });

            var error = await Record.ExceptionAsync(
                () => lease.ReplaceAsync(memory.Memory, CancellationToken.None));

            Assert.True(hardLinkAttempted);
            Assert.False(
                hardLinkCreated,
                $"The temporary file was exposed through a hard link (Win32 {hardLinkError}).");
            Assert.Null(error);
            Assert.False(File.Exists(exposedLink));
            Assert.Equal(
                "replacement instructions",
                await File.ReadAllTextAsync(instructions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(replacement);
            if (File.Exists(exposedLink))
            {
                File.Delete(exposedLink);
            }
        }
    }

    [Fact]
    public async Task ReplaceAsync_PreservesNewContentWhenReplacementCannotBeMoved()
    {
        Directory.CreateDirectory(_root);
        var instructions = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(instructions, "original instructions");
        var replacement = Encoding.UTF8.GetBytes("replacement instructions");
        var operations = new PartialFailureReplacementOperations(1176);
        try
        {
            using var lease = WindowsWorkspaceFileLease.OpenForWrite(
                _root,
                "AGENTS.md",
                WorkspaceContextService.MaximumReadableFileBytes,
                operations);

            var error = await Assert.ThrowsAsync<WorkspaceFileReplacementException>(
                () => lease.ReplaceAsync(replacement, CancellationToken.None));

            Assert.Equal(1176, error.NativeErrorCode);
            Assert.True(operations.BackupPathWasProvided);
            Assert.Equal("original instructions", await File.ReadAllTextAsync(instructions));
            var recovery = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".AGENTS.md.agentdesk-*.tmp"));
            Assert.Equal("replacement instructions", await File.ReadAllTextAsync(recovery));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(replacement);
        }
    }

    [Fact]
    public async Task ReplaceAsync_RestoresOriginalAndPreservesNewContentAfterPartialMove()
    {
        Directory.CreateDirectory(_root);
        var instructions = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(instructions, "original instructions");
        var replacement = Encoding.UTF8.GetBytes("replacement instructions");
        var operations = new PartialFailureReplacementOperations(1177);
        try
        {
            using var lease = WindowsWorkspaceFileLease.OpenForWrite(
                _root,
                "AGENTS.md",
                WorkspaceContextService.MaximumReadableFileBytes,
                operations);

            var error = await Assert.ThrowsAsync<WorkspaceFileReplacementException>(
                () => lease.ReplaceAsync(replacement, CancellationToken.None));

            Assert.Equal(1177, error.NativeErrorCode);
            Assert.True(operations.BackupPathWasProvided);
            Assert.Equal("original instructions", await File.ReadAllTextAsync(instructions));
            var recovery = Assert.Single(Directory.EnumerateFiles(
                _root,
                ".AGENTS.md.agentdesk-*.tmp"));
            Assert.Equal("replacement instructions", await File.ReadAllTextAsync(recovery));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(replacement);
        }
    }

    [Fact]
    public async Task WriteInstructionFileAsync_PreservesWindowsSecurityOwnerAndAttributes()
    {
        Directory.CreateDirectory(_root);
        var instructions = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(instructions, "old instructions");
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var identity = currentIdentity.User ??
            throw new InvalidOperationException("The current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(new FileInfo(instructions), security);
        File.SetAttributes(
            instructions,
            FileAttributes.Archive | FileAttributes.NotContentIndexed);
        var originalSecurity = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(instructions),
            AccessControlSections.Access | AccessControlSections.Owner);
        var originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(
            AccessControlSections.Access | AccessControlSections.Owner);
        var originalOwner = Assert.IsType<SecurityIdentifier>(
            originalSecurity.GetOwner(typeof(SecurityIdentifier))).Value;
        var originalAttributes = File.GetAttributes(instructions);
        var service = new WorkspaceContextService();

        await service.WriteInstructionFileAsync(
            _root,
            "AGENTS.md",
            "updated instructions");

        var updatedSecurity = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(instructions),
            AccessControlSections.Access | AccessControlSections.Owner);
        Assert.Equal("updated instructions", await File.ReadAllTextAsync(instructions));
        Assert.Equal(
            originalSddl,
            updatedSecurity.GetSecurityDescriptorSddlForm(
                AccessControlSections.Access | AccessControlSections.Owner));
        Assert.Equal(
            originalOwner,
            Assert.IsType<SecurityIdentifier>(
                updatedSecurity.GetOwner(typeof(SecurityIdentifier))).Value);
        Assert.Equal(originalAttributes, File.GetAttributes(instructions));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The junction helper could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"The test junction could not be created: {standardError}{standardOutput}");
    }

    private static void CreateHardLink(string link, string target)
    {
        if (!CreateHardLinkNative(link, target, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The test hard link could not be created.");
        }
    }

    private sealed class PinCallbackMemoryManager(byte[] bytes, Action onFirstPin)
        : MemoryManager<byte>
    {
        private int _accessCount;

        public override Span<byte> GetSpan()
        {
            ObserveAccess();
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObserveAccess();
            return bytes.AsMemory(elementIndex).Pin();
        }

        private void ObserveAccess()
        {
            if (Interlocked.Increment(ref _accessCount) == 2)
            {
                onFirstPin();
            }
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkNative(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class PartialFailureReplacementOperations(int nativeErrorCode) :
        IWorkspaceFileReplacementOperations
    {
        public bool BackupPathWasProvided { get; private set; }

        public void Replace(string targetPath, string replacementPath, string? backupPath)
        {
            BackupPathWasProvided = backupPath is not null;
            if (nativeErrorCode == 1177)
            {
                File.Move(
                    targetPath,
                    backupPath ?? targetPath + ".agentdesk-test-displaced");
            }
            else if (nativeErrorCode == 1176 && backupPath is null)
            {
                File.Move(targetPath, targetPath + ".agentdesk-test-displaced");
            }

            throw new WorkspaceFileReplacementException(
                "Simulated partial replacement failure.",
                nativeErrorCode);
        }

        public void Move(string sourcePath, string targetPath) =>
            File.Move(sourcePath, targetPath);
    }
}
