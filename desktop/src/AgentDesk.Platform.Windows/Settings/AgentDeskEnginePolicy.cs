using System.Text;

namespace AgentDesk.Platform.Windows.Settings;

public static class AgentDeskEnginePolicy
{
    private static readonly byte[] PolicyBytes =
        Encoding.UTF8.GetBytes("[features]\nremote_fetch = false\n");

    public static string DefaultEngineDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AgentDesk",
        "Engine");

    public static async Task EnsureAsync(
        string? engineDataPath = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(engineDataPath ?? DefaultEngineDataPath);
        Directory.CreateDirectory(directory);
        var policyPath = Path.Combine(directory, "requirements.toml");
        var temporaryPath = $"{policyPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(PolicyBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, policyPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
