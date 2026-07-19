namespace AgentDesk.App.Bridge;

public enum DesktopFileDialogKind
{
    Open,
    Save,
}

public sealed record DesktopFileDialogRequest(
    DesktopFileDialogKind Kind,
    string Operation,
    string SuggestedFileName,
    string FileExtension);
