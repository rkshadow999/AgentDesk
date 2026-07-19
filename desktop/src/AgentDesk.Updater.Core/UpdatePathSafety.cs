namespace AgentDesk.Updater.Core;

public static class UpdatePathSafety
{
    public static string FullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsContained(string root, string candidate)
    {
        var normalizedRoot = FullPath(root);
        var normalizedCandidate = FullPath(candidate);
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsureContained(string root, string candidate, string description)
    {
        if (!IsContained(root, candidate))
        {
            throw new UpdateSecurityException($"The {description} escaped its trusted root.");
        }
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var fullPath = FullPath(path);
        FileSystemInfo? current = File.Exists(fullPath)
            ? new FileInfo(fullPath)
            : new DirectoryInfo(fullPath);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UpdateSecurityException("Update paths cannot traverse reparse points.");
            }

            current = current switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        }
    }
}
