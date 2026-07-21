using System.Security.Cryptography;
using AgentDesk.Updater.Core;

return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] arguments)
{
    using var cancellationSource = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellationSource.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    try
    {
        var command = UpdaterCommandLine.Parse(arguments);
        return command switch
        {
            ApplyUpdateCommand apply => await ApplyAsync(
                apply,
                cancellationSource.Token).ConfigureAwait(false),
            RecoverUpdateCommand recover => await RecoverAsync(
                recover,
                cancellationSource.Token).ConfigureAwait(false),
            _ => 2,
        };
    }
    catch (UpdaterCommandLineException)
    {
        Console.Error.WriteLine("{\"status\":\"failed\",\"error\":\"invalid-command\"}");
        return 2;
    }
    catch (UpdateSecurityException)
    {
        Console.Error.WriteLine("{\"status\":\"failed\",\"error\":\"security-check\"}");
        return 3;
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("{\"status\":\"failed\",\"error\":\"parent-timeout\"}");
        return 4;
    }
    catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
    {
        Console.Error.WriteLine("{\"status\":\"cancelled\"}");
        return 5;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("{\"status\":\"failed\",\"error\":\"update-failed\"}");
        return 6;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static async Task<int> ApplyAsync(
    ApplyUpdateCommand command,
    CancellationToken cancellationToken)
{
    var publicKey = await ReadPublicKeyAsync(
        command.PublicKeyFile,
        cancellationToken).ConfigureAwait(false);
    try
    {
        using var service = new PortableUpdateService(UpdateOriginPolicy.Default);
        var staged = await service.CheckAndStageAsync(
            new UpdateCheckRequest(
                command.ManifestUri,
                command.SignatureUri,
                publicKey,
                command.CurrentVersion,
                command.Architecture,
                command.AllowPrerelease,
                command.StateDirectory),
            cancellationToken).ConfigureAwait(false);
        if (staged is null)
        {
            Console.WriteLine("{\"status\":\"no-update\"}");
            return 0;
        }

        await new PortableUpdateInstaller().InstallAsync(
            new PortableInstallRequest(
                command.InstallationDirectory,
                command.StateDirectory,
                staged.PayloadDirectory,
                staged.Manifest.Version,
                staged.Asset.EntryPoint,
                command.ParentProcessId,
                command.ParentExitTimeout,
                command.RestartArguments),
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine("{\"status\":\"updated\"}");
        return 0;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(publicKey);
    }
}

static async Task<int> RecoverAsync(
    RecoverUpdateCommand command,
    CancellationToken cancellationToken)
{
    var recovered = await new PortableUpdateInstaller().RecoverAsync(
        command.InstallationDirectory,
        command.StateDirectory,
        cancellationToken).ConfigureAwait(false);
    Console.WriteLine(recovered
        ? "{\"status\":\"recovered\"}"
        : "{\"status\":\"clean\"}");
    return 0;
}

static async Task<byte[]> ReadPublicKeyAsync(
    string path,
    CancellationToken cancellationToken)
{
    var fullPath = Path.GetFullPath(path);
    var information = new FileInfo(fullPath);
    if (!information.Exists ||
        information.Length is < 32 or > 1024 ||
        (information.Attributes & FileAttributes.ReparsePoint) != 0)
    {
        throw new UpdateSecurityException("The update public key file is invalid.");
    }

    var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
    if (bytes.Length != information.Length)
    {
        CryptographicOperations.ZeroMemory(bytes);
        throw new UpdateSecurityException("The update public key file changed while it was being read.");
    }

    return bytes;
}
