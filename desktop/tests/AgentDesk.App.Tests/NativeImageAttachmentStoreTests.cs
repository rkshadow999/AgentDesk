using AgentDesk.App.Attachments;
using AgentDesk.App.Bridge;

namespace AgentDesk.App.Tests;

public sealed class NativeImageAttachmentStoreTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task StageAndResolveAsync_ReturnsMetadataThenConsumesValidatedNativeContent()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, OnePixelPng);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);

        var staged = await store.StageAsync([sourcePath]);

        var reference = Assert.Single(staged.Attachments);
        Assert.Matches("^[0-9A-F]{64}$", reference.Token);
        Assert.Equal("pixel.png", reference.Name);
        Assert.Equal("image/png", reference.MimeType);
        Assert.Equal(OnePixelPng.Length, reference.Size);
        Assert.Null(staged.Error);
        Assert.DoesNotContain(
            Convert.ToBase64String(OnePixelPng),
            System.Text.Json.JsonSerializer.Serialize(reference),
            StringComparison.Ordinal);

        var resolved = await store.ResolveAndConsumeAsync([reference]);

        var attachment = Assert.Single(resolved);
        Assert.Equal(Convert.ToBase64String(OnePixelPng), attachment.Base64Data);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ResolveAndConsumeAsync([reference]));
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ResolveAndConsumeAsync_RevalidatesTheStagedSignatureAndClearsTamperedData()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, OnePixelPng);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var staged = await store.StageAsync([sourcePath]);
        var reference = Assert.Single(staged.Attachments);
        var stagedPath = Assert.Single(
            Directory.EnumerateFiles(
                stagingDirectory.FullName,
                "*.image",
                SearchOption.AllDirectories));
        await File.WriteAllBytesAsync(stagedPath, "not-an-image"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ResolveAndConsumeAsync([reference]));

        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StageAsync_RejectsMismatchedExtensionsWithoutRetainingFileContent()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "fake.png");
        await File.WriteAllBytesAsync(sourcePath, "GIF89a"u8.ToArray());
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);

        var result = await store.StageAsync([sourcePath]);

        Assert.Empty(result.Attachments);
        Assert.Equal(ImageAttachmentError.ContentMismatch, result.Error);
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DiscardAsync_DeletesOnlyTheRequestedNativeAttachment()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var firstPath = Path.Combine(sourceDirectory.FullName, "first.png");
        var secondPath = Path.Combine(sourceDirectory.FullName, "second.png");
        await File.WriteAllBytesAsync(firstPath, OnePixelPng);
        await File.WriteAllBytesAsync(secondPath, OnePixelPng);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var staged = await store.StageAsync([firstPath, secondPath]);

        var first = Assert.Single(staged.Attachments, item => item.Name == "first.png");
        var second = Assert.Single(staged.Attachments, item => item.Name == "second.png");
        await store.DiscardAsync([first.Token]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ResolveAndConsumeAsync([first]));
        var resolved = await store.ResolveAndConsumeAsync([second]);
        Assert.Equal("second.png", Assert.Single(resolved).Name);
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StageAsync_RejectsOversizedFilesBeforeRetainingContent()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "oversized.png");
        await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write))
        {
            source.SetLength(NativeImageAttachmentStore.MaximumAttachmentBytes + 1L);
        }
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);

        var result = await store.StageAsync([sourcePath]);

        Assert.Equal(ImageAttachmentError.TooLarge, result.Error);
        Assert.Empty(result.Attachments);
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StageAsync_RejectsDuplicateNamesCaseInsensitively()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var firstDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory.FullName, "a"));
        var secondDirectory = Directory.CreateDirectory(Path.Combine(sourceDirectory.FullName, "b"));
        var firstPath = Path.Combine(firstDirectory.FullName, "pixel.png");
        var secondPath = Path.Combine(secondDirectory.FullName, "PIXEL.PNG");
        await File.WriteAllBytesAsync(firstPath, OnePixelPng);
        await File.WriteAllBytesAsync(secondPath, OnePixelPng);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);

        var result = await store.StageAsync([firstPath, secondPath]);

        Assert.Equal(ImageAttachmentError.DuplicateName, result.Error);
        Assert.Equal("pixel.png", Assert.Single(result.Attachments).Name);
    }

    [Fact]
    public async Task ClearAsync_DeletesEveryStagedAttachmentAndRejectsReplay()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, OnePixelPng);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        var reference = Assert.Single((await store.StageAsync([sourcePath])).Attachments);

        await store.ClearAsync();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.ResolveAndConsumeAsync([reference]));
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ClearAsync_RetriesADeletionThatWasTemporarilyBlocked()
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, "pixel.png");
        await File.WriteAllBytesAsync(sourcePath, OnePixelPng);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);
        _ = await store.StageAsync([sourcePath]);
        var stagedPath = Assert.Single(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));

        File.SetAttributes(stagedPath, FileAttributes.ReadOnly);
        Exception? deletionError;
        try
        {
            deletionError = await Record.ExceptionAsync(() => store.ClearAsync());
        }
        finally
        {
            if (File.Exists(stagedPath))
            {
                File.SetAttributes(stagedPath, FileAttributes.Normal);
            }
        }
        Assert.IsType<IOException>(deletionError);
        await store.ClearAsync();
        Assert.Empty(Directory.EnumerateFiles(
            stagingDirectory.FullName,
            "*.image",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Constructor_RemovesAnAbandonedWindowStagingDirectory()
    {
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var abandoned = Directory.CreateDirectory(Path.Combine(
            stagingDirectory.FullName,
            "window-0123456789ABCDEF0123456789ABCDEF"));
        await File.WriteAllBytesAsync(
            Path.Combine(abandoned.FullName, "stale.image"),
            OnePixelPng);

        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);

        Assert.False(Directory.Exists(abandoned.FullName));
    }

    public static TheoryData<string, byte[]> TruncatedOrSignaturePaddedImages => new()
    {
        {
            "truncated.png",
            [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]
        },
        {
            "padded.jpg",
            [0xff, 0xd8, 0xff, 0x00, 0x01, 0x02, 0xff, 0xd9]
        },
        {
            "truncated.gif",
            "GIF89a"u8.ToArray()
        },
        {
            "empty.webp",
            [0x52, 0x49, 0x46, 0x46, 0x04, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50]
        },
    };

    [Theory]
    [MemberData(nameof(TruncatedOrSignaturePaddedImages))]
    public async Task StageAsync_RejectsTruncatedOrSignaturePaddedImages(
        string fileName,
        byte[] bytes)
    {
        using var sourceDirectory = new AttachmentTestDirectory("agentdesk-image-source-");
        using var stagingDirectory = new AttachmentTestDirectory("agentdesk-image-stage-");
        var sourcePath = Path.Combine(sourceDirectory.FullName, fileName);
        await File.WriteAllBytesAsync(sourcePath, bytes);
        await using var store = new NativeImageAttachmentStore(stagingDirectory.FullName);

        var result = await store.StageAsync([sourcePath]);

        Assert.Equal(ImageAttachmentError.ContentMismatch, result.Error);
        Assert.Empty(result.Attachments);
    }
}
