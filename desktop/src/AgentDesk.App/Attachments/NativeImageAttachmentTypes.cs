namespace AgentDesk.App.Attachments;

public sealed record NativeImageAttachmentReference(
    string Token,
    string Name,
    string MimeType,
    long Size);

public enum ImageAttachmentError
{
    UnsupportedType,
    TooMany,
    TooLarge,
    TotalTooLarge,
    DuplicateName,
    ContentMismatch,
    ReadFailed,
}

public sealed record NativeImageAttachmentStageResult(
    IReadOnlyList<NativeImageAttachmentReference> Attachments,
    ImageAttachmentError? Error = null);
