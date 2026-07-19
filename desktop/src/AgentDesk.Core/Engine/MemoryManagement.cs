namespace AgentDesk.Core.Engine;

public sealed record MemoryFileId
{
    private const int MaximumSessionFileNameLength = 255;

    public MemoryFileId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is not ("global" or "workspace"))
        {
            const string prefix = "session/";
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("The memory file ID is invalid.", nameof(value));
            }

            var fileName = value[prefix.Length..];
            if (fileName.Length is 0 or > MaximumSessionFileNameLength ||
                !fileName.EndsWith(".md", StringComparison.Ordinal) ||
                fileName.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            {
                throw new ArgumentException("The memory file ID is invalid.", nameof(value));
            }
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum MemoryFileScope
{
    Global,
    Workspace,
    Session,
}

public sealed record MemoryManagementCapabilities(
    int SchemaVersion,
    bool List,
    bool Read,
    bool Write,
    bool Delete,
    bool MutationConfirmationRequired)
{
    public static MemoryManagementCapabilities Unsupported { get; } = new(
        0,
        List: false,
        Read: false,
        Write: false,
        Delete: false,
        MutationConfirmationRequired: false);
}

public sealed record MemoryFileDescriptor(
    MemoryFileId Id,
    MemoryFileScope Scope,
    string Name,
    ulong ByteLength,
    DateTimeOffset? ModifiedAt,
    bool Writable);

public sealed record MemoryFileListing(
    IReadOnlyList<MemoryFileDescriptor> Files,
    bool Truncated);

public sealed record MemoryFileDocument(
    MemoryFileDescriptor File,
    string Content);

public enum MemoryMutationStatus
{
    ConfirmationRequired,
    Success,
    NotFound,
}

public sealed record MemoryMutationResult(
    MemoryMutationStatus Status,
    string Message,
    MemoryFileDescriptor? File);
