namespace AgentDesk.Core.Engine;

public static class PromptAttachmentPolicy
{
    public const int MaximumAttachmentCount = 4;
    public const int MaximumAttachmentBytes = 10 * 1024 * 1024;
    public const int MaximumTotalBytes = 20 * 1024 * 1024;
    public const int MaximumAttachmentNameLength = 255;
    public const int MaximumAttachmentBase64Length = ((MaximumAttachmentBytes + 2) / 3) * 4;

    public static int Validate(IReadOnlyList<PromptAttachment> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count > MaximumAttachmentCount)
        {
            throw new ArgumentException(
                $"A prompt can contain at most {MaximumAttachmentCount} images.",
                nameof(attachments));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        foreach (var attachment in attachments)
        {
            ArgumentNullException.ThrowIfNull(attachment);
            if (!names.Add(attachment.Name))
            {
                throw new ArgumentException(
                    "Image attachment names must be unique.",
                    nameof(attachments));
            }

            totalBytes = checked(totalBytes + ValidateSingle(attachment));
            if (totalBytes > MaximumTotalBytes)
            {
                throw new ArgumentException(
                    "The combined image attachment size exceeds 20 MiB.",
                    nameof(attachments));
            }
        }

        return totalBytes;
    }

    private static int ValidateSingle(PromptAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.Name) ||
            attachment.Name.Length > MaximumAttachmentNameLength ||
            attachment.Name.Any(char.IsControl) ||
            attachment.Name.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("The image attachment name is invalid.", nameof(attachment));
        }
        if (attachment.Base64Data.Length == 0 ||
            attachment.Base64Data.Length > MaximumAttachmentBase64Length ||
            attachment.Base64Data.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The image attachment data is invalid.", nameof(attachment));
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(attachment.Base64Data);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "The image attachment data is not valid Base64.",
                nameof(attachment),
                exception);
        }

        if (bytes.Length == 0 || bytes.Length > MaximumAttachmentBytes ||
            !HasExpectedImageSignature(attachment.MimeType, bytes))
        {
            throw new ArgumentException(
                "The image attachment MIME type does not match its content.",
                nameof(attachment));
        }
        return bytes.Length;
    }

    private static bool HasExpectedImageSignature(string mimeType, ReadOnlySpan<byte> bytes) =>
        mimeType switch
        {
            "image/png" => bytes.StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12 &&
                bytes[..4].SequenceEqual("RIFF"u8) &&
                bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
}
