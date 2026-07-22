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

/// <summary>
/// In-memory image payload from WebView paste/drop. Base64 is host-only staging input.
/// </summary>
public sealed record NativeImageAttachmentPayload(
    string Name,
    string MimeType,
    string Base64Data);
