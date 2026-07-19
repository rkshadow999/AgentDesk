namespace AgentDesk.Cloud.Client;

public sealed class EncryptedHandoffCoordinatorOptions
{
    public const int DefaultMaximumDocumentBytes =
        CloudSyncCoordinatorOptions.DefaultMaximumDocumentBytes;

    public EncryptedHandoffCoordinatorOptions(
        int maximumDocumentBytes = DefaultMaximumDocumentBytes)
    {
        if (maximumDocumentBytes is < 1 or > SessionSyncDocument.MaximumDocumentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDocumentBytes));
        }

        MaximumDocumentBytes = maximumDocumentBytes;
    }

    public int MaximumDocumentBytes { get; }
}
