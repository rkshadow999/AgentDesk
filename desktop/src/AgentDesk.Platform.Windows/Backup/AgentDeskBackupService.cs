using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentDesk.Platform.Windows.IO;
using Microsoft.Win32.SafeHandles;

namespace AgentDesk.Platform.Windows.Backup;

public sealed class AgentDeskBackupService
{
    private const int SchemaVersion = 1;
    private const int MaximumFileCount = 100_000;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    private const string ManifestEntryName = "manifest.json";
    private const string DataPrefix = "data/";
    private static readonly HashSet<string> ExcludedTopLevelDirectories = new(
        ["AttachmentStaging", "WebView2", "Updates"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
    private readonly RestoreValidationTestHooks _restoreValidationTestHooks;

    public AgentDeskBackupService()
        : this(new RestoreValidationTestHooks())
    {
    }

    internal AgentDeskBackupService(RestoreValidationTestHooks restoreValidationTestHooks)
    {
        _restoreValidationTestHooks =
            restoreValidationTestHooks ??
            throw new ArgumentNullException(nameof(restoreValidationTestHooks));
    }

    public async Task<AgentDeskBackupResult> CreateAsync(
        string sourceDirectory,
        string destinationFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFile);
        var sourceRoot = NormalizeDirectory(sourceDirectory);
        var destinationPath = WindowsHandleFileSystem.ValidateLocalPath(destinationFile);
        if (IsWithin(sourceRoot, destinationPath))
        {
            throw new ArgumentException(
                "The backup file must be outside the AgentDesk data directory.",
                nameof(destinationFile));
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new ArgumentException("The backup destination is invalid.", nameof(destinationFile));
        using var sourceGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
            sourceRoot,
            createIfMissing: false);
        using var destinationGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
            destinationDirectory,
            createIfMissing: true);
        sourceGuard.Validate();
        destinationGuard.Validate();
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        var temporaryHandle = WindowsHandleFileSystem.CreateTemporaryFile(temporaryPath);
        await using var output = new FileStream(
            temporaryHandle,
            FileAccess.ReadWrite,
            64 * 1024,
            isAsync: true);
        var state = new BackupCreationState();
        var renamed = false;

        try
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                await AddDirectoryToArchiveAsync(
                    sourceRoot,
                    relativeDirectory: string.Empty,
                    archive,
                    state,
                    cancellationToken).ConfigureAwait(false);

                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                manifestEntry.ExternalAttributes = 0;
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(
                    manifestStream,
                    new BackupManifest(
                        SchemaVersion,
                        DateTimeOffset.UtcNow,
                        state.ManifestFiles),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            sourceGuard.Validate();
            destinationGuard.Validate();
            WindowsHandleFileSystem.AtomicReplace(
                temporaryHandle,
                temporaryPath,
                destinationPath);
            renamed = true;
            destinationGuard.Validate();
            return new AgentDeskBackupResult(
                state.ManifestFiles.Count,
                state.TotalBytes);
        }
        finally
        {
            if (!renamed)
            {
                try
                {
                    WindowsHandleFileSystem.DeleteTemporaryFile(temporaryHandle);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public async Task<AgentDeskBackupResult> RestoreAsync(
        string backupFile,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var backupPath = WindowsHandleFileSystem.ValidateLocalPath(backupFile);
        var destinationRoot = NormalizeDirectory(destinationDirectory);
        if (IsWithin(destinationRoot, backupPath))
        {
            throw new ArgumentException(
                "The backup file must be outside the restore destination.",
                nameof(backupFile));
        }

        var backupDirectory = Path.GetDirectoryName(backupPath) ??
            throw new ArgumentException("The backup source is invalid.", nameof(backupFile));
        using var backupGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
            backupDirectory,
            createIfMissing: false);
        using var backupHandle = WindowsHandleFileSystem.OpenExistingFileForRead(backupPath);
        backupGuard.Validate();
        var parent = Directory.GetParent(destinationRoot)?.FullName ??
            throw new ArgumentException("The restore destination is invalid.", nameof(destinationDirectory));
        using var parentGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
            parent,
            createIfMissing: true);
        var stagingRoot = Path.Combine(parent, $".agentdesk-restore-{Guid.NewGuid():N}");
        WindowsHandleFileSystem.DirectoryPathGuard? stagingGuard = null;
        RestorePublicationLease? publicationLease = null;
        WindowsHandleFileSystem.FileIdentity? stagingIdentity = null;
        var stagingMoved = false;

        try
        {
            stagingGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                stagingRoot,
                createIfMissing: true);
            stagingIdentity = stagingGuard.LeafIdentity;
            publicationLease = new RestorePublicationLease(stagingRoot, parent);
            var result = await ExtractVerifiedAsync(
                backupHandle,
                stagingRoot,
                publicationLease,
                cancellationToken).ConfigureAwait(false);
            publicationLease.Seal();
            stagingGuard.Validate();
            stagingIdentity = stagingGuard.LeafIdentity;
            stagingGuard.Dispose();
            stagingGuard = null;

            using var stagingHandle =
                WindowsHandleFileSystem.OpenExistingDirectoryForReplacement(
                    stagingRoot,
                    stagingIdentity);
            parentGuard.Validate();
            using var displaced = WindowsHandleFileSystem.AtomicReplaceDirectory(
                stagingHandle,
                stagingRoot,
                destinationRoot,
                _restoreValidationTestHooks.BeforePublish is null
                    ? null
                    : () => _restoreValidationTestHooks.BeforePublish(stagingRoot),
                () => publicationLease.AcquirePublishedValidationLease(
                    destinationRoot,
                    _restoreValidationTestHooks));
            stagingMoved = true;
            parentGuard.Validate();
            if (displaced is not null)
            {
                TryDeleteDirectory(displaced.Handle, displaced.Path);
            }
            return result;
        }
        finally
        {
            stagingGuard?.Dispose();
            publicationLease?.Dispose();
            if (!stagingMoved && stagingIdentity is not null)
            {
                try
                {
                    using var stagingHandle =
                        WindowsHandleFileSystem.OpenExistingDirectoryForReplacement(
                            stagingRoot,
                            stagingIdentity);
                    TryDeleteDirectory(stagingHandle, stagingRoot);
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static async Task<AgentDeskBackupResult> ExtractVerifiedAsync(
        SafeFileHandle backupHandle,
        string stagingRoot,
        RestorePublicationLease publicationLease,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            backupHandle,
            FileAccess.Read,
            64 * 1024,
            isAsync: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > MaximumFileCount + 1)
        {
            throw new InvalidDataException("The backup entry count is invalid.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            ValidateArchiveEntry(entry);
            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException("The backup contains duplicate entries.");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry) ||
            manifestEntry.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("The backup manifest is missing or invalid.");
        }

        BackupManifest? manifest;
        try
        {
            await using var manifestStream = manifestEntry.Open();
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(
                manifestStream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The backup manifest is invalid.", exception);
        }
        if (manifest is null || manifest.SchemaVersion != SchemaVersion ||
            manifest.Files is null ||
            manifest.Files.Count > MaximumFileCount)
        {
            throw new InvalidDataException("The backup manifest version or file count is invalid.");
        }

        var expectedEntries = new HashSet<string>(StringComparer.Ordinal) { ManifestEntryName };
        long totalBytes = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateManifestFile(file);
            var archiveName = DataPrefix + file.Path;
            if (!expectedEntries.Add(archiveName) || !entries.TryGetValue(archiveName, out var entry))
            {
                throw new InvalidDataException("The backup manifest contains a duplicate or missing file.");
            }
            if (entry.Length != file.Length)
            {
                throw new InvalidDataException("A backup file length does not match its manifest.");
            }
            totalBytes = checked(totalBytes + file.Length);
            if (totalBytes > MaximumTotalBytes)
            {
                throw new InvalidDataException("The backup exceeds the total size limit.");
            }

            var outputPath = Path.GetFullPath(
                Path.Combine(stagingRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(stagingRoot, outputPath))
            {
                throw new InvalidDataException("The backup contains an unsafe output path.");
            }
            using var outputDirectoryGuard =
                WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                    Path.GetDirectoryName(outputPath)!,
                    createIfMissing: true);
            await using var entryStream = entry.Open();
            SafeFileHandle? outputHandle = WindowsHandleFileSystem.CreateTemporaryFile(outputPath);
            var verified = false;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                var copied = await CopyAndHashToHandleAsync(
                    entryStream,
                    outputHandle,
                    hash,
                    MaximumFileBytes,
                    cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(outputHandle);
                var expectedHash = Convert.FromHexString(file.Sha256);
                if (copied != file.Length ||
                    !CryptographicOperations.FixedTimeEquals(
                        expectedHash,
                        hash.GetHashAndReset()))
                {
                    throw new InvalidDataException(
                        "A backup file failed integrity validation.");
                }
                outputDirectoryGuard.Validate();
                var pin = publicationLease.PrepareFilePin(
                    file.Path,
                    outputPath,
                    outputHandle,
                    file.Length,
                    expectedHash);
                outputHandle.Dispose();
                outputHandle = null;
                publicationLease.ActivateFilePin(pin);
                verified = true;
            }
            finally
            {
                if (!verified)
                {
                    try
                    {
                        if (outputHandle is not null)
                        {
                            WindowsHandleFileSystem.DeleteTemporaryFile(outputHandle);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                outputHandle?.Dispose();
            }
        }

        if (!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expectedEntries))
        {
            throw new InvalidDataException("The backup contains unlisted entries.");
        }
        return new AgentDeskBackupResult(manifest.Files.Count, totalBytes);
    }

    private static async Task AddDirectoryToArchiveAsync(
        string directory,
        string relativeDirectory,
        ZipArchive archive,
        BackupCreationState state,
        CancellationToken cancellationToken)
    {
        foreach (var itemPath in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var opened = WindowsHandleFileSystem.OpenExistingEntryForRead(itemPath);
            opened.Validate();
            var name = Path.GetFileName(itemPath);
            var relativePath = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : $"{relativeDirectory}/{name}";
            if (!IsCanonicalRelativePath(relativePath))
            {
                throw new InvalidDataException(
                    "The AgentDesk data directory contains an unsafe path.");
            }

            if (opened.IsDirectory)
            {
                if (string.IsNullOrEmpty(relativeDirectory) &&
                    ExcludedTopLevelDirectories.Contains(name))
                {
                    continue;
                }
                await AddDirectoryToArchiveAsync(
                    itemPath,
                    relativePath,
                    archive,
                    state,
                    cancellationToken).ConfigureAwait(false);
                opened.Validate();
                continue;
            }

            if (state.ManifestFiles.Count >= MaximumFileCount)
            {
                throw new InvalidDataException("The backup contains too many files.");
            }
            await using var input = new FileStream(
                opened.Handle,
                FileAccess.Read,
                64 * 1024,
                isAsync: false);
            var length = input.Length;
            if (length > MaximumFileBytes)
            {
                throw new InvalidDataException("A backup file exceeds the size limit.");
            }

            state.TotalBytes = checked(state.TotalBytes + length);
            if (state.TotalBytes > MaximumTotalBytes)
            {
                throw new InvalidDataException("The backup exceeds the total size limit.");
            }

            var entry = archive.CreateEntry(
                DataPrefix + relativePath,
                CompressionLevel.Optimal);
            entry.ExternalAttributes = 0;
            await using var entryStream = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var copied = await CopyAndHashAsync(
                input,
                entryStream,
                hash,
                MaximumFileBytes,
                cancellationToken).ConfigureAwait(false);
            if (copied != length)
            {
                throw new IOException("An AgentDesk data file changed during backup.");
            }
            state.ManifestFiles.Add(new BackupFile(
                relativePath,
                copied,
                Convert.ToHexString(hash.GetHashAndReset())));
        }

    }

    private sealed class BackupCreationState
    {
        internal List<BackupFile> ManifestFiles { get; } = [];

        internal long TotalBytes { get; set; }
    }

    private sealed class RestorePublicationLease : IDisposable
    {
        private readonly List<RestorePublicationEntry> _entries = [];
        private readonly Dictionary<string, WindowsHandleFileSystem.FileIdentity>
            _directoryIdentities = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _expectedPaths =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directoryPaths =
            new(StringComparer.OrdinalIgnoreCase) { string.Empty };
        private readonly WindowsHandleFileSystem.DirectoryPathGuard _pinGuard;
        private readonly WindowsHandleFileSystem.FileIdentity _pinIdentity;
        private readonly string _pinRoot;
        private readonly string _stagingRoot;
        private int _disposed;
        private int _nextPinIndex;
        private bool _sealed;

        internal RestorePublicationLease(string stagingRoot, string parent)
        {
            _stagingRoot = stagingRoot;
            _pinRoot = Path.Combine(parent, $".agentdesk-restore-pins-{Guid.NewGuid():N}");
            _pinGuard = WindowsHandleFileSystem.DirectoryPathGuard.Acquire(
                _pinRoot,
                createIfMissing: true);
            _pinIdentity = _pinGuard.LeafIdentity;
        }

        internal PreparedRestoreFilePin PrepareFilePin(
            string relativePath,
            string outputPath,
            SafeFileHandle outputHandle,
            long expectedLength,
            byte[] expectedSha256)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_sealed || !_expectedPaths.Add(relativePath))
            {
                throw new InvalidDataException(
                    "The backup contains colliding local paths.");
            }

            AddParentDirectories(relativePath);
            var identity = WindowsHandleFileSystem.GetIdentity(outputHandle);
            var pinPath = Path.Combine(
                _pinRoot,
                $"{_nextPinIndex++:D8}.pin");
            _pinGuard.Validate();
            WindowsHandleFileSystem.CreateHardLinkPin(pinPath, outputPath);
            _pinGuard.Validate();
            return new PreparedRestoreFilePin(
                pinPath,
                relativePath,
                identity,
                expectedLength,
                expectedSha256);
        }

        internal void ActivateFilePin(PreparedRestoreFilePin pin)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            SafeFileHandle? handle = WindowsHandleFileSystem.OpenPinnedFile(
                pin.PinPath,
                pin.Identity);
            try
            {
                var entry = new RestorePublicationEntry(
                    handle,
                    pin.RelativePath,
                    pin.ExpectedLength,
                    pin.ExpectedSha256);
                ValidateFileContents(entry);
                _entries.Add(entry);
                handle = null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        internal void Seal()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_sealed)
            {
                throw new InvalidOperationException("The restore publication lease is already sealed.");
            }

            foreach (var relativePath in _directoryPaths
                         .OrderBy(static path => path.Count(character => character == '/'))
                         .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (relativePath.Length > 0 && !_expectedPaths.Add(relativePath))
                {
                    throw new InvalidDataException(
                        "The backup contains colliding local paths.");
                }
                var directoryPath = relativePath.Length == 0
                    ? _stagingRoot
                    : Path.Combine(
                        _stagingRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                _directoryIdentities.Add(
                    relativePath,
                    WindowsHandleFileSystem.GetDirectoryIdentity(directoryPath));
            }

            _sealed = true;
            ValidateStagingTree(_stagingRoot);
        }

        internal IDisposable AcquirePublishedValidationLease(
            string destinationRoot,
            RestoreValidationTestHooks testHooks)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_sealed)
            {
                throw new InvalidOperationException("The restore publication lease is not sealed.");
            }

            var validationLease = new PublishedTreeValidationLease(
                WindowsHandleFileSystem.WatchDirectoryTree(destinationRoot));
            try
            {
                testHooks.AfterReplacementMoved?.Invoke(destinationRoot);
                foreach (var entry in _entries)
                {
                    var expectedPath = Path.Combine(
                        destinationRoot,
                        entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    var publishedHandle = WindowsHandleFileSystem.OpenPublishedFileLease(
                        entry.Handle,
                        expectedPath);
                    validationLease.Add(publishedHandle);
                    WindowsHandleFileSystem.ValidateNoAlternateDataStreams(expectedPath);
                    testHooks.AfterPublishedFilePinned?.Invoke(expectedPath);
                    ValidateFileContents(entry with { Handle = publishedHandle });
                }

                foreach (var directory in _directoryIdentities)
                {
                    var expectedPath = directory.Key.Length == 0
                        ? destinationRoot
                        : Path.Combine(
                            destinationRoot,
                            directory.Key.Replace('/', Path.DirectorySeparatorChar));
                    WindowsHandleFileSystem.ValidatePublishedDirectoryIdentity(
                        expectedPath,
                        directory.Value);
                    WindowsHandleFileSystem.ValidateNoAlternateDataStreams(expectedPath);
                }

                var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var relativeDirectory in _directoryPaths)
                {
                    var directoryPath = relativeDirectory.Length == 0
                        ? destinationRoot
                        : Path.Combine(
                            destinationRoot,
                            relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                    foreach (var itemPath in Directory.EnumerateFileSystemEntries(directoryPath))
                    {
                        var itemName = Path.GetFileName(itemPath);
                        var relativePath = relativeDirectory.Length == 0
                            ? itemName
                            : $"{relativeDirectory}/{itemName}";
                        if (!actualPaths.Add(relativePath) || !_expectedPaths.Contains(relativePath))
                        {
                            throw new InvalidDataException(
                                "The verified restore tree changed before publication.");
                        }
                    }
                    testHooks.AfterDirectoryEnumerated?.Invoke(directoryPath);
                }
                if (!actualPaths.SetEquals(_expectedPaths))
                {
                    throw new InvalidDataException(
                        "The verified restore tree changed before publication.");
                }
                return validationLease;
            }
            catch
            {
                validationLease.Abort();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                _entries[index].Handle.Dispose();
            }
            _pinGuard.Dispose();
            try
            {
                using var pinHandle =
                    WindowsHandleFileSystem.OpenExistingDirectoryForReplacement(
                        _pinRoot,
                        _pinIdentity);
                TryDeleteDirectory(pinHandle, _pinRoot);
            }
            catch (Exception)
            {
            }
        }

        private void AddParentDirectories(string relativePath)
        {
            var separatorIndex = relativePath.LastIndexOf('/');
            while (separatorIndex >= 0)
            {
                var directory = relativePath[..separatorIndex];
                _directoryPaths.Add(directory);
                separatorIndex = directory.LastIndexOf('/');
            }
        }

        private void ValidateStagingTree(string root)
        {
            _pinGuard.Validate();
            foreach (var entry in _entries)
            {
                var expectedPath = Path.Combine(
                    root,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                WindowsHandleFileSystem.ValidatePublishedFileIdentity(
                    entry.Handle,
                    expectedPath);
                WindowsHandleFileSystem.ValidateNoAlternateDataStreams(expectedPath);
                ValidateFileContents(entry);
            }
            foreach (var directory in _directoryIdentities)
            {
                var expectedPath = directory.Key.Length == 0
                    ? root
                    : Path.Combine(
                        root,
                        directory.Key.Replace('/', Path.DirectorySeparatorChar));
                WindowsHandleFileSystem.ValidatePublishedDirectoryIdentity(
                    expectedPath,
                    directory.Value);
                WindowsHandleFileSystem.ValidateNoAlternateDataStreams(expectedPath);
            }

            var actualPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relativeDirectory in _directoryPaths)
            {
                var directoryPath = relativeDirectory.Length == 0
                    ? root
                    : Path.Combine(
                        root,
                        relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
                foreach (var itemPath in Directory.EnumerateFileSystemEntries(directoryPath))
                {
                    var itemName = Path.GetFileName(itemPath);
                    var relativePath = relativeDirectory.Length == 0
                        ? itemName
                        : $"{relativeDirectory}/{itemName}";
                    if (!actualPaths.Add(relativePath) || !_expectedPaths.Contains(relativePath))
                    {
                        throw new InvalidDataException(
                            "The verified restore tree changed before publication.");
                    }
                }
            }
            if (!actualPaths.SetEquals(_expectedPaths))
            {
                throw new InvalidDataException(
                    "The verified restore tree changed before publication.");
            }
            _pinGuard.Validate();
        }

        private static void ValidateFileContents(RestorePublicationEntry entry)
        {
            if (RandomAccess.GetLength(entry.Handle) != entry.ExpectedLength)
            {
                throw new InvalidDataException(
                    "The verified restore file changed before publication.");
            }

            var buffer = new byte[64 * 1024];
            long offset = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (offset < entry.ExpectedLength)
            {
                var read = RandomAccess.Read(
                    entry.Handle,
                    buffer,
                    offset);
                if (read == 0)
                {
                    throw new InvalidDataException(
                        "The verified restore file changed before publication.");
                }
                hash.AppendData(buffer, 0, read);
                offset = checked(offset + read);
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    entry.ExpectedSha256,
                    hash.GetHashAndReset()))
            {
                throw new InvalidDataException(
                    "The verified restore file changed before publication.");
            }
        }
    }

    private sealed record RestorePublicationEntry(
        SafeFileHandle Handle,
        string RelativePath,
        long ExpectedLength,
        byte[] ExpectedSha256);

    private sealed record PreparedRestoreFilePin(
        string PinPath,
        string RelativePath,
        WindowsHandleFileSystem.FileIdentity Identity,
        long ExpectedLength,
        byte[] ExpectedSha256);

    private sealed class PublishedTreeValidationLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        private WindowsHandleFileSystem.DirectoryChangeMonitor? _changeMonitor;
        private int _disposed;

        internal PublishedTreeValidationLease(
            WindowsHandleFileSystem.DirectoryChangeMonitor changeMonitor)
        {
            _changeMonitor = changeMonitor;
        }

        internal void Add(SafeFileHandle handle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _handles.Add(handle);
        }

        internal void Abort()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            DisposeHandles();
            _changeMonitor?.Dispose();
            _changeMonitor = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            DisposeHandles();
            var monitor = _changeMonitor;
            _changeMonitor = null;
            monitor?.ValidateNoChangesAndDispose();
        }

        private void DisposeHandles()
        {
            for (var index = _handles.Count - 1; index >= 0; index--)
            {
                _handles[index].Dispose();
            }
            _handles.Clear();
        }
    }

    internal sealed record RestoreValidationTestHooks(
        Action<string>? BeforePublish = null,
        Action<string>? AfterReplacementMoved = null,
        Action<string>? AfterPublishedFilePinned = null,
        Action<string>? AfterDirectoryEnumerated = null);

    private static async Task<long> CopyAndHashAsync(
        Stream input,
        Stream output,
        IncrementalHash hash,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("A backup file exceeds the size limit.");
            }
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> CopyAndHashToHandleAsync(
        Stream input,
        SafeFileHandle outputHandle,
        IncrementalHash hash,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }
            var offset = total;
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("A backup file exceeds the size limit.");
            }
            hash.AppendData(buffer, 0, read);
            await RandomAccess.WriteAsync(
                outputHandle,
                buffer.AsMemory(0, read),
                offset,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateArchiveEntry(ZipArchiveEntry entry)
    {
        if (entry.FullName.Length == 0 || entry.FullName.Length > 32_767 ||
            entry.FullName.Contains('\\') || entry.FullName.StartsWith('/') ||
            entry.FullName.EndsWith('/') ||
            (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
            !IsCanonicalRelativePath(entry.FullName))
        {
            throw new InvalidDataException("The backup contains an unsafe archive entry.");
        }
    }

    private static void ValidateManifestFile(BackupFile file)
    {
        if (file is null || file.Path is null || file.Sha256 is null ||
            !IsCanonicalRelativePath(file.Path) ||
            file.Length is < 0 or > MaximumFileBytes ||
            file.Sha256.Length != 64 ||
            !file.Sha256.All(static character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            throw new InvalidDataException("The backup manifest contains an invalid file.");
        }
    }

    private static bool IsCanonicalRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path))
        {
            return false;
        }
        var segments = path.Split('/');
        return segments.All(segment =>
            segment.Length > 0 && segment is not "." and not ".." &&
            !segment.Contains(':') && !segment.Any(char.IsControl));
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(WindowsHandleFileSystem.ValidateLocalPath(path));

    private static void TryDeleteDirectory(
        SafeFileHandle directoryHandle,
        string path)
    {
        try
        {
            WindowsHandleFileSystem.DeleteDirectoryTree(directoryHandle, path);
        }
        catch (Exception)
        {
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private sealed record BackupManifest(
        int SchemaVersion,
        DateTimeOffset CreatedAt,
        IReadOnlyList<BackupFile> Files);

    private sealed record BackupFile(string Path, long Length, string Sha256);
}

public sealed record AgentDeskBackupResult(int FileCount, long TotalBytes);
