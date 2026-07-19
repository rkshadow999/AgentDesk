namespace AgentDesk.Cloud.Client;

public sealed class CloudSyncCoordinatorOptions
{
    public const int DefaultMaximumDocumentBytes =
        CloudConnectionOptions.DefaultMaximumEnvelopeBytes - 16;

    public CloudSyncCoordinatorOptions(
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
