namespace AgentDesk.Engine.Sidecar;

public sealed class SidecarProcessStartInfo
{
    private readonly Dictionary<string, string?> _environment;

    public SidecarProcessStartInfo(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string engineWorkspacePath,
        IReadOnlyDictionary<string, string?> environment)
    {
        FileName = fileName;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        EngineWorkspacePath = engineWorkspacePath;
        _environment = new Dictionary<string, string?>(
            environment,
            StringComparer.OrdinalIgnoreCase);
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string WorkingDirectory { get; }

    public string EngineWorkspacePath { get; }

    public IReadOnlyDictionary<string, string?> Environment => _environment;

    internal void ForgetApiKey()
    {
        _environment["XAI_API_KEY"] = null;
        _environment["GROK_CODE_XAI_API_KEY"] = null;
    }
}
