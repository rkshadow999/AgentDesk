namespace AgentDesk.Core.Engine;

public sealed record PromptAttachment(
    string Name,
    string MimeType,
    string Base64Data);
