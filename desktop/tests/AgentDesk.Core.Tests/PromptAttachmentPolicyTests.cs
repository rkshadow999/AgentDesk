using AgentDesk.Core.Engine;

namespace AgentDesk.Core.Tests;

public sealed class PromptAttachmentPolicyTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public void Validate_AcceptsSupportedImagesAndReturnsDecodedByteCount()
    {
        var totalBytes = PromptAttachmentPolicy.Validate(
            [new PromptAttachment("pixel.png", "image/png", OnePixelPng)]);

        Assert.Equal(Convert.FromBase64String(OnePixelPng).Length, totalBytes);
    }

    [Theory]
    [InlineData("text/plain", "aGVsbG8=")]
    [InlineData("image/png", "not-base64")]
    [InlineData("image/png", "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==")]
    public void Validate_RejectsUnsupportedOrMismatchedPayloadsWithoutEchoingData(
        string mimeType,
        string base64Data)
    {
        var error = Assert.Throws<ArgumentException>(() => PromptAttachmentPolicy.Validate(
            [new PromptAttachment("image.bin", mimeType, base64Data)]));

        Assert.DoesNotContain(base64Data, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsMoreThanFourImages()
    {
        var attachment = new PromptAttachment("pixel.png", "image/png", OnePixelPng);

        Assert.Throws<ArgumentException>(() => PromptAttachmentPolicy.Validate(
            [attachment, attachment, attachment, attachment, attachment]));
    }

    [Fact]
    public void Validate_RejectsAnImageLargerThanTenMib()
    {
        var bytes = new byte[PromptAttachmentPolicy.MaximumAttachmentBytes + 1];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4e;
        bytes[3] = 0x47;
        bytes[4] = 0x0d;
        bytes[5] = 0x0a;
        bytes[6] = 0x1a;
        bytes[7] = 0x0a;

        Assert.Throws<ArgumentException>(() => PromptAttachmentPolicy.Validate(
            [new PromptAttachment("oversized.png", "image/png", Convert.ToBase64String(bytes))]));
    }

    [Fact]
    public void Validate_RejectsDuplicateNames()
    {
        var attachment = new PromptAttachment("pixel.png", "image/png", OnePixelPng);

        Assert.Throws<ArgumentException>(() => PromptAttachmentPolicy.Validate(
            [attachment, attachment]));
    }
}
