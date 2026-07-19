namespace AgentDesk.App.Tests;

internal sealed class AttachmentTestDirectory : IDisposable
{
    public AttachmentTestDirectory(string prefix)
    {
        FullName = Directory.CreateTempSubdirectory(prefix).FullName;
    }

    public string FullName { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullName, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
