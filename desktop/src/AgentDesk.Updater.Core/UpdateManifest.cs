namespace AgentDesk.Updater.Core;

public enum UpdateArchitecture
{
    X64,
    Arm64,
}

public sealed record UpdateAsset(
    UpdateArchitecture Architecture,
    Uri Uri,
    string Sha256Hex,
    long Size,
    string EntryPoint);

public sealed record UpdateManifest(
    int SchemaVersion,
    string Product,
    SemanticVersion Version,
    IReadOnlyList<UpdateAsset> Assets);
