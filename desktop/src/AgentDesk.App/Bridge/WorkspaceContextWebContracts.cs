using AgentDesk.App.Workspace;

namespace AgentDesk.App.Bridge;

public sealed record WorkspaceInstructionsListWebCommand(
    string RequestId,
    int WorkspaceGeneration) : WebCommand;

public sealed record WorkspaceFileReadWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string RelativePath) : WebCommand;

public sealed record WorkspaceInstructionsWriteWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string RelativePath,
    string Content) : WebCommand;

public sealed record WorkspaceFileSearchWebCommand(
    string RequestId,
    int WorkspaceGeneration,
    string Query) : WebCommand;

public sealed record WorkspaceInstructionsListWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    IReadOnlyList<WorkspaceContextFile> Files) : WebEvent;

public sealed record WorkspaceFileReadWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string RelativePath,
    string Content) : WebEvent;

public sealed record WorkspaceInstructionsWriteWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string RelativePath) : WebEvent;

public sealed record WorkspaceFileSearchWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    string Query,
    IReadOnlyList<WorkspaceContextFile> Files) : WebEvent;

public enum WorkspaceContextOperation
{
    InstructionsList,
    FileRead,
    InstructionsWrite,
    FileSearch,
}

public sealed record WorkspaceContextErrorWebEvent(
    string RequestId,
    int WorkspaceGeneration,
    WorkspaceContextOperation Operation) : WebEvent;
