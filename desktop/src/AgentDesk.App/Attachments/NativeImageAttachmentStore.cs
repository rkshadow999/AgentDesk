using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AgentDesk.Core.Engine;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentDesk.App.Attachments;

public sealed class NativeImageAttachmentStore : IAsyncDisposable
{
    public const int MaximumAttachmentCount = 4;
    public const int MaximumAttachmentBytes = 10 * 1024 * 1024;
    public const int MaximumTotalBytes = 20 * 1024 * 1024;
    public const int MaximumAttachmentNameLength = 255;
    public const int AttachmentTokenLength = 64;

    private const uint MaximumImageFrames = 256;
    private const ulong MaximumDecodedPixelsPerFrame = 40_000_000;
    private const ulong MaximumDecodedPixelsTotal = 64_000_000;

    private readonly string _stagingDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StagedAttachment> _attachments =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingDeletionPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private FileStream? _leaseStream;
    private int _disposeState;

    public NativeImageAttachmentStore(string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        if (!Path.IsPathFullyQualified(stagingRoot))
        {
            throw new ArgumentException(
                "The image attachment staging root must be fully qualified.",
                nameof(stagingRoot));
        }

        var fullRoot = Path.GetFullPath(stagingRoot);
        Directory.CreateDirectory(fullRoot);
        EnsureRegularDirectory(fullRoot);
        using var cleanupLock = AcquireCleanupLock(fullRoot);
        CleanupAbandonedStagingDirectories(fullRoot);

        _stagingDirectory = Path.Combine(fullRoot, $"window-{NewDirectoryToken()}");
        try
        {
            Directory.CreateDirectory(_stagingDirectory);
            EnsureRegularDirectory(_stagingDirectory);
            _leaseStream = new FileStream(
                Path.Combine(_stagingDirectory, ".lease"),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read);
        }
        catch
        {
            try
            {
                Directory.Delete(_stagingDirectory, recursive: true);
            }
            catch
            {
            }
            throw;
        }
    }

    public async Task<NativeImageAttachmentStageResult> StageAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var addedTokens = new List<string>();
        try
        {
            ThrowIfDisposed();
            EnsureRegularDirectory(_stagingDirectory);
            RetryPendingDeletionsUnsafe();

            foreach (var sourcePath in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_attachments.Count >= MaximumAttachmentCount)
                {
                    return ResultUnsafe(ImageAttachmentError.TooMany);
                }
                if (string.IsNullOrWhiteSpace(sourcePath) ||
                    !Path.IsPathFullyQualified(sourcePath))
                {
                    return ResultUnsafe(ImageAttachmentError.ReadFailed);
                }

                var fullSourcePath = Path.GetFullPath(sourcePath);
                var name = Path.GetFileName(fullSourcePath);
                var mimeType = MimeTypeForName(name);
                if (mimeType is null)
                {
                    return ResultUnsafe(ImageAttachmentError.UnsupportedType);
                }
                if (!IsValidName(name))
                {
                    return ResultUnsafe(ImageAttachmentError.ReadFailed);
                }
                if (_attachments.Values.Any(item =>
                        string.Equals(
                            item.Reference.Name,
                            name,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return ResultUnsafe(ImageAttachmentError.DuplicateName);
                }

                byte[] bytes;
                try
                {
                    EnsureRegularFile(fullSourcePath);
                    var length = new FileInfo(fullSourcePath).Length;
                    if (length is <= 0 or > MaximumAttachmentBytes)
                    {
                        return ResultUnsafe(ImageAttachmentError.TooLarge);
                    }
                    if (CurrentTotalBytesUnsafe() + length > MaximumTotalBytes)
                    {
                        return ResultUnsafe(ImageAttachmentError.TotalTooLarge);
                    }
                    bytes = await ReadBoundedFileAsync(
                            fullSourcePath,
                            MaximumAttachmentBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsFileReadFailure(exception))
                {
                    return ResultUnsafe(ImageAttachmentError.ReadFailed);
                }

                var stageError = await StageBytesUnsafeAsync(
                        name,
                        mimeType,
                        bytes,
                        addedTokens,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (stageError is not null)
                {
                    return ResultUnsafe(stageError);
                }
            }

            return ResultUnsafe();
        }
        catch (OperationCanceledException)
        {
            RollbackAddedTokensUnsafe(addedTokens);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stage images that arrived as in-memory payloads (clipboard paste / drag-drop).
    /// Base64 is decoded only on the native host so WebView never keeps raw image bodies.
    /// </summary>
    public async Task<NativeImageAttachmentStageResult> StagePayloadsAsync(
        IReadOnlyList<NativeImageAttachmentPayload> payloads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var addedTokens = new List<string>();
        try
        {
            ThrowIfDisposed();
            EnsureRegularDirectory(_stagingDirectory);
            RetryPendingDeletionsUnsafe();

            foreach (var payload in payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_attachments.Count >= MaximumAttachmentCount)
                {
                    return ResultUnsafe(ImageAttachmentError.TooMany);
                }
                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.Name) ||
                    string.IsNullOrWhiteSpace(payload.MimeType) ||
                    string.IsNullOrWhiteSpace(payload.Base64Data))
                {
                    return ResultUnsafe(ImageAttachmentError.ReadFailed);
                }

                var name = Path.GetFileName(payload.Name.Trim());
                var mimeType = MimeTypeForName(name);
                if (mimeType is null ||
                    !string.Equals(mimeType, payload.MimeType.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return ResultUnsafe(ImageAttachmentError.UnsupportedType);
                }
                if (!IsValidName(name))
                {
                    return ResultUnsafe(ImageAttachmentError.ReadFailed);
                }
                if (_attachments.Values.Any(item =>
                        string.Equals(
                            item.Reference.Name,
                            name,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return ResultUnsafe(ImageAttachmentError.DuplicateName);
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(payload.Base64Data);
                }
                catch (FormatException)
                {
                    return ResultUnsafe(ImageAttachmentError.ReadFailed);
                }

                if (bytes.LongLength is <= 0 or > MaximumAttachmentBytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return ResultUnsafe(ImageAttachmentError.TooLarge);
                }
                if (CurrentTotalBytesUnsafe() + bytes.LongLength > MaximumTotalBytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    return ResultUnsafe(ImageAttachmentError.TotalTooLarge);
                }

                var stageError = await StageBytesUnsafeAsync(
                        name,
                        mimeType,
                        bytes,
                        addedTokens,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (stageError is not null)
                {
                    return ResultUnsafe(stageError);
                }
            }

            return ResultUnsafe();
        }
        catch (OperationCanceledException)
        {
            RollbackAddedTokensUnsafe(addedTokens);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ImageAttachmentError?> StageBytesUnsafeAsync(
        string name,
        string mimeType,
        byte[] bytes,
        List<string> addedTokens,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await IsValidImageAsync(mimeType, bytes, cancellationToken).ConfigureAwait(false))
            {
                return ImageAttachmentError.ContentMismatch;
            }

            var token = NewToken();
            var stagedPath = Path.Combine(_stagingDirectory, token + ".image");
            var committed = false;
            try
            {
                await WriteNewFileAsync(stagedPath, bytes, cancellationToken).ConfigureAwait(false);
                var reference = new NativeImageAttachmentReference(
                    token,
                    name,
                    mimeType,
                    bytes.LongLength);
                _attachments.Add(
                    token,
                    new StagedAttachment(reference, stagedPath, SHA256.HashData(bytes)));
                addedTokens.Add(token);
                committed = true;
                return null;
            }
            catch (Exception exception) when (IsFileReadFailure(exception))
            {
                return ImageAttachmentError.ReadFailed;
            }
            finally
            {
                if (!committed)
                {
                    DeleteOrQueueUnsafe(stagedPath);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private void RollbackAddedTokensUnsafe(List<string> addedTokens)
    {
        foreach (var token in addedTokens)
        {
            if (_attachments.Remove(token, out var staged))
            {
                DeleteStagedAttachment(staged);
            }
        }
        RetryPendingDeletionsUnsafe();
    }

    public async Task<IReadOnlyList<PromptAttachment>> ResolveAndConsumeAsync(
        IReadOnlyList<NativeImageAttachmentReference> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var consumed = new List<StagedAttachment>();
        try
        {
            ThrowIfDisposed();
            ValidateReferenceSet(references);
            EnsureRegularDirectory(_stagingDirectory);

            foreach (var reference in references)
            {
                if (!_attachments.TryGetValue(reference.Token, out var staged))
                {
                    throw new InvalidDataException(
                        "The image attachment token is unknown or has already been consumed.");
                }
                consumed.Add(staged);
                if (staged.Reference != reference)
                {
                    throw new InvalidDataException(
                        "The image attachment metadata does not match the native staging record.");
                }
            }

            var resolved = new List<PromptAttachment>(consumed.Count);
            foreach (var staged in consumed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes;
                try
                {
                    EnsureRegularFile(staged.Path);
                    bytes = await ReadBoundedFileAsync(
                            staged.Path,
                            MaximumAttachmentBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsFileReadFailure(exception))
                {
                    throw new InvalidDataException(
                        "The native image attachment could not be read.",
                        exception);
                }

                try
                {
                    var hash = SHA256.HashData(bytes);
                    try
                    {
                        if (bytes.LongLength != staged.Reference.Size ||
                            !CryptographicOperations.FixedTimeEquals(hash, staged.Sha256) ||
                            !await IsValidImageAsync(
                                    staged.Reference.MimeType,
                                    bytes,
                                    cancellationToken)
                                .ConfigureAwait(false))
                        {
                            throw new InvalidDataException(
                                "The native image attachment changed after it was selected.");
                        }
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(hash);
                    }

                    resolved.Add(new PromptAttachment(
                        staged.Reference.Name,
                        staged.Reference.MimeType,
                        Convert.ToBase64String(bytes)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            try
            {
                _ = PromptAttachmentPolicy.Validate(resolved);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "The native image attachments failed prompt validation.",
                    exception);
            }
            return resolved;
        }
        finally
        {
            try
            {
                foreach (var staged in consumed)
                {
                    _attachments.Remove(staged.Reference.Token);
                    DeleteStagedAttachment(staged);
                }
                RetryPendingDeletionsUnsafe();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task DiscardAsync(
        IReadOnlyList<string> tokens,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count > MaximumAttachmentCount)
        {
            throw new InvalidDataException("Too many image attachment tokens were supplied.");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            foreach (var token in tokens.Distinct(StringComparer.Ordinal))
            {
                ValidateToken(token);
                if (_attachments.Remove(token, out var staged))
                {
                    DeleteStagedAttachment(staged);
                }
            }
            RetryPendingDeletionsUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ClearUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var previousState = Interlocked.CompareExchange(ref _disposeState, 1, 0);
        if (previousState == 2)
        {
            return;
        }
        if (previousState != 0)
        {
            throw new InvalidOperationException(
                "Image attachment disposal is already in progress.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        var completed = false;
        try
        {
            ClearUnsafe();
            _leaseStream?.Dispose();
            _leaseStream = null;
            DeleteRequiredFile(Path.Combine(_stagingDirectory, ".lease"));
            try
            {
                Directory.Delete(_stagingDirectory, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
            }
            completed = true;
            Volatile.Write(ref _disposeState, 2);
        }
        catch
        {
            Volatile.Write(ref _disposeState, 0);
            throw;
        }
        finally
        {
            _gate.Release();
            if (completed)
            {
                _gate.Dispose();
            }
        }
    }

    private NativeImageAttachmentStageResult ResultUnsafe(
        ImageAttachmentError? error = null) => new(
        _attachments.Values.Select(item => item.Reference).ToArray(),
        error);

    private long CurrentTotalBytesUnsafe() =>
        _attachments.Values.Sum(item => item.Reference.Size);

    private void ClearUnsafe()
    {
        foreach (var staged in _attachments.Values)
        {
            DeleteStagedAttachment(staged);
        }
        _attachments.Clear();
        RetryPendingDeletionsUnsafe();
    }

    private static void ValidateReferenceSet(
        IReadOnlyList<NativeImageAttachmentReference> references)
    {
        if (references.Count > MaximumAttachmentCount)
        {
            throw new InvalidDataException("Too many image attachment references were supplied.");
        }

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var reference in references)
        {
            ArgumentNullException.ThrowIfNull(reference);
            ValidateToken(reference.Token);
            if (!tokens.Add(reference.Token) || !names.Add(reference.Name) ||
                !IsValidName(reference.Name) ||
                MimeTypeForName(reference.Name) != reference.MimeType ||
                reference.Size is <= 0 or > MaximumAttachmentBytes)
            {
                throw new InvalidDataException("An image attachment reference is invalid.");
            }
            totalBytes = checked(totalBytes + reference.Size);
            if (totalBytes > MaximumTotalBytes)
            {
                throw new InvalidDataException(
                    "The image attachment references exceed the total size limit.");
            }
        }
    }

    private static void ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != AttachmentTokenLength ||
            token.Any(character =>
                !char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F')))
        {
            throw new InvalidDataException("The image attachment token is invalid.");
        }
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= MaximumAttachmentNameLength &&
        !name.Any(char.IsControl) &&
        name.IndexOfAny(['/', '\\']) < 0;

    private static string? MimeTypeForName(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => null,
        };

    private static bool HasCompleteImageStructure(string mimeType, ReadOnlySpan<byte> bytes) =>
        mimeType switch
        {
            "image/png" => HasCompletePngStructure(bytes),
            "image/jpeg" => HasCompleteJpegStructure(bytes),
            "image/gif" => HasCompleteGifStructure(bytes),
            "image/webp" => HasCompleteWebpStructure(bytes),
            _ => false,
        };

    private static async Task<bool> IsValidImageAsync(
        string mimeType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (!HasCompleteImageStructure(mimeType, bytes))
        {
            return false;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                _ = await writer.StoreAsync();
                _ = writer.DetachStream();
            }
            stream.Seek(0);
            cancellationToken.ThrowIfCancellationRequested();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.DecoderInformation.CodecId != DecoderIdForMimeType(mimeType) ||
                decoder.FrameCount is 0 or > MaximumImageFrames)
            {
                return false;
            }

            ulong totalPixels = 0;
            for (uint index = 0; index < decoder.FrameCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = await decoder.GetFrameAsync(index);
                var pixels = checked((ulong)frame.PixelWidth * frame.PixelHeight);
                if (pixels is 0 or > MaximumDecodedPixelsPerFrame ||
                    checked(totalPixels + pixels) > MaximumDecodedPixelsTotal)
                {
                    return false;
                }
                totalPixels += pixels;

                var pixelProvider = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8,
                    BitmapAlphaMode.Straight,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                var pixelBytes = pixelProvider.DetachPixelData();
                try
                {
                    if ((ulong)pixelBytes.LongLength != checked(pixels * 4))
                    {
                        return false;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pixelBytes);
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    private static Guid DecoderIdForMimeType(string mimeType) => mimeType switch
    {
        "image/png" => BitmapDecoder.PngDecoderId,
        "image/jpeg" => BitmapDecoder.JpegDecoderId,
        "image/gif" => BitmapDecoder.GifDecoderId,
        "image/webp" => BitmapDecoder.WebpDecoderId,
        _ => Guid.Empty,
    };

    private static bool HasCompletePngStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 45 ||
            !bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return false;
        }

        var offset = 8;
        var firstChunk = true;
        var sawImageData = false;
        while (offset + 12 <= bytes.Length)
        {
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (dataLength > int.MaxValue)
            {
                return false;
            }
            var chunkLength = checked((int)dataLength);
            var nextOffset = (long)offset + 12 + chunkLength;
            if (nextOffset > bytes.Length)
            {
                return false;
            }

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, chunkLength);
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                bytes.Slice(offset + 8 + chunkLength, 4));
            if (storedCrc != ComputePngCrc(bytes.Slice(offset + 4, 4 + chunkLength)))
            {
                return false;
            }

            if (firstChunk)
            {
                if (!type.SequenceEqual("IHDR"u8) || chunkLength != 13 ||
                    BinaryPrimitives.ReadUInt32BigEndian(data[..4]) == 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)) == 0 ||
                    data[10] != 0 || data[11] != 0 || data[12] > 1)
                {
                    return false;
                }
                firstChunk = false;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return chunkLength == 0 && sawImageData && nextOffset == bytes.Length;
            }

            offset = checked((int)nextOffset);
        }
        return false;
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
            }
        }
        return ~crc;
    }

    private static bool HasCompleteJpegStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || !bytes.StartsWith(new byte[] { 0xff, 0xd8 }) ||
            !bytes[^2..].SequenceEqual(new byte[] { 0xff, 0xd9 }))
        {
            return false;
        }

        var offset = 2;
        var sawFrame = false;
        while (offset < bytes.Length - 2)
        {
            if (bytes[offset++] != 0xff)
            {
                return false;
            }
            while (offset < bytes.Length && bytes[offset] == 0xff)
            {
                offset++;
            }
            if (offset >= bytes.Length)
            {
                return false;
            }

            var marker = bytes[offset++];
            if (marker == 0xda)
            {
                if (!TryReadJpegSegment(bytes, ref offset, out _))
                {
                    return false;
                }
                return sawFrame;
            }
            if (marker is 0x01 or (>= 0xd0 and <= 0xd8))
            {
                continue;
            }
            if (!TryReadJpegSegment(bytes, ref offset, out var segment))
            {
                return false;
            }
            if (IsJpegStartOfFrame(marker))
            {
                if (segment.Length < 6 ||
                    BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(1, 2)) == 0 ||
                    BinaryPrimitives.ReadUInt16BigEndian(segment.Slice(3, 2)) == 0)
                {
                    return false;
                }
                sawFrame = true;
            }
        }
        return false;
    }

    private static bool TryReadJpegSegment(
        ReadOnlySpan<byte> bytes,
        ref int offset,
        out ReadOnlySpan<byte> segment)
    {
        segment = default;
        if (offset + 2 > bytes.Length)
        {
            return false;
        }
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
        if (length < 2 || offset + length > bytes.Length)
        {
            return false;
        }
        segment = bytes.Slice(offset + 2, length - 2);
        offset += length;
        return true;
    }

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        (>= 0xc0 and <= 0xc3) or
        (>= 0xc5 and <= 0xc7) or
        (>= 0xc9 and <= 0xcb) or
        (>= 0xcd and <= 0xcf);

    private static bool HasCompleteGifStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14 ||
            !(bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)))
        {
            return false;
        }

        var offset = 6;
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)) == 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset + 2, 2)) == 0)
        {
            return false;
        }
        var packed = bytes[offset + 4];
        offset += 7;
        if ((packed & 0x80) != 0)
        {
            var colorTableBytes = 3 * (1 << ((packed & 0x07) + 1));
            if (offset + colorTableBytes > bytes.Length)
            {
                return false;
            }
            offset += colorTableBytes;
        }

        var sawImage = false;
        while (offset < bytes.Length)
        {
            switch (bytes[offset++])
            {
                case 0x2c:
                    if (offset + 9 > bytes.Length)
                    {
                        return false;
                    }
                    var imagePacked = bytes[offset + 8];
                    offset += 9;
                    if ((imagePacked & 0x80) != 0)
                    {
                        var localTableBytes = 3 * (1 << ((imagePacked & 0x07) + 1));
                        if (offset + localTableBytes > bytes.Length)
                        {
                            return false;
                        }
                        offset += localTableBytes;
                    }
                    if (offset >= bytes.Length || bytes[offset++] is < 2 or > 12 ||
                        !SkipGifSubBlocks(bytes, ref offset))
                    {
                        return false;
                    }
                    sawImage = true;
                    break;
                case 0x21:
                    if (offset >= bytes.Length)
                    {
                        return false;
                    }
                    offset++;
                    if (!SkipGifSubBlocks(bytes, ref offset))
                    {
                        return false;
                    }
                    break;
                case 0x3b:
                    return sawImage && offset == bytes.Length;
                default:
                    return false;
            }
        }
        return false;
    }

    private static bool SkipGifSubBlocks(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            var length = bytes[offset++];
            if (length == 0)
            {
                return true;
            }
            if (offset + length > bytes.Length)
            {
                return false;
            }
            offset += length;
        }
        return false;
    }

    private static bool HasCompleteWebpStructure(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8) ||
            !bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
        {
            return false;
        }

        var offset = 12;
        var sawImageChunk = false;
        while (offset + 8 <= bytes.Length)
        {
            var type = bytes.Slice(offset, 4);
            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var paddedLength = (long)dataLength + (dataLength & 1);
            var nextOffset = (long)offset + 8 + paddedLength;
            if (nextOffset > bytes.Length)
            {
                return false;
            }
            if (type.SequenceEqual("VP8 "u8) || type.SequenceEqual("VP8L"u8) ||
                type.SequenceEqual("ANMF"u8))
            {
                sawImageChunk = true;
            }
            offset = checked((int)nextOffset);
        }
        return sawImageChunk && offset == bytes.Length;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException("The image attachment size is invalid.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("The image attachment changed while it was read.");
            }
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The image attachment staging directory is not a regular directory.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("The image attachment is not a regular file.");
        }
    }

    private static bool IsFileReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
            NotSupportedException or ArgumentException;

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(AttachmentTokenLength / 2));

    private static string NewDirectoryToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private void DeleteStagedAttachment(StagedAttachment staged)
    {
        DeleteOrQueueUnsafe(staged.Path);
        CryptographicOperations.ZeroMemory(staged.Sha256);
    }

    private void DeleteOrQueueUnsafe(string path)
    {
        try
        {
            File.Delete(path);
            _pendingDeletionPaths.Remove(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _pendingDeletionPaths.Add(path);
        }
    }

    private void RetryPendingDeletionsUnsafe()
    {
        foreach (var path in _pendingDeletionPaths.ToArray())
        {
            DeleteOrQueueUnsafe(path);
        }
        if (_pendingDeletionPaths.Count > 0)
        {
            throw new IOException(
                "One or more native image attachment files could not be deleted.");
        }
    }

    private static void DeleteRequiredFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }

    private static FileStream AcquireCleanupLock(string stagingRoot)
    {
        var path = Path.Combine(stagingRoot, ".cleanup.lock");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (attempt < 99)
            {
                Thread.Sleep(10);
            }
        }
        throw new IOException("The image attachment cleanup lock could not be acquired.");
    }

    private static void CleanupAbandonedStagingDirectories(string stagingRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     stagingRoot,
                     "window-*",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                EnsureRegularDirectory(directory);
                var leasePath = Path.Combine(directory, ".lease");
                if (File.Exists(leasePath))
                {
                    using (new FileStream(
                               leasePath,
                               FileMode.Open,
                               FileAccess.ReadWrite,
                               FileShare.None))
                    {
                    }
                }
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
    }

    private sealed record StagedAttachment(
        NativeImageAttachmentReference Reference,
        string Path,
        byte[] Sha256);
}
