using System.Text;
using Microsoft.Extensions.Options;

namespace AgentDesk.Cloud;

internal sealed class CloudDatabaseLease : IDisposable
{
    private readonly string _lockPath;
    private readonly FileStream _stream;

    public CloudDatabaseLease(IOptions<CloudOptions> options)
    {
        var databasePath = CloudDatabasePathPolicy.NormalizeAndValidate(options.Value.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _lockPath = databasePath + ".service.lock";
        var fileOptions = FileOptions.WriteThrough;
        if (OperatingSystem.IsWindows())
        {
            fileOptions |= FileOptions.DeleteOnClose;
        }
        try
        {
            _stream = new FileStream(
                _lockPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                fileOptions);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "The AgentDesk Cloud database is already leased by another service or maintenance process.",
                error);
        }

        try
        {
            var leaseText = $"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}\n";
            _stream.Write(Encoding.UTF8.GetBytes(leaseText));
            _stream.Flush(flushToDisk: true);
        }
        catch
        {
            _stream.Dispose();
            File.Delete(_lockPath);
            throw;
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(_lockPath);
        }
    }
}
