using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class ReleaseManifestIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"AgentDesk.ReleaseManifest.Tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task GeneratedUpdaterManifestVerifiesAndStagesTheUpdaterArchive()
    {
        Directory.CreateDirectory(_directory);
        const string version = "1.2.3";
        var x64PackagePath = Path.Combine(
            _directory,
            $"AgentDesk-{version}-win-x64-portable.zip");
        var arm64PackagePath = Path.Combine(
            _directory,
            $"AgentDesk-{version}-win-arm64-portable.zip");
        var x64UpdaterPath = Path.Combine(
            _directory,
            $"AgentDesk-{version}-win-x64-updater.zip");
        var arm64UpdaterPath = Path.Combine(
            _directory,
            $"AgentDesk-{version}-win-arm64-updater.zip");
        CreateArchive(x64PackagePath, "AgentDesk.App.exe", [0x01]);
        CreateArchive(arm64PackagePath, "AgentDesk.App.exe", [0x02]);
        var x64Updater = CreateMinimalPe(machine: 0x8664);
        CreateArchive(x64UpdaterPath, "AgentDesk.Updater.exe", x64Updater);
        CreateArchive(
            arm64UpdaterPath,
            "AgentDesk.Updater.exe",
            CreateMinimalPe(machine: 0xaa64));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKeyPath = Path.Combine(_directory, "update-private-key.pk8");
        var publicKeyPath = Path.Combine(_directory, "update-public-key.spki");
        var outputDirectory = Path.Combine(_directory, "metadata");
        await File.WriteAllBytesAsync(privateKeyPath, key.ExportPkcs8PrivateKey());
        await File.WriteAllBytesAsync(publicKeyPath, key.ExportSubjectPublicKeyInfo());

        var repositoryRoot = FindRepositoryRoot();
        var generatorPath = Path.Combine(
            repositoryRoot,
            "scripts",
            "agentdesk",
            "New-AgentDeskUpdateManifest.ps1");
        var result = await RunGeneratorAsync(
            generatorPath,
            version,
            x64PackagePath,
            arm64PackagePath,
            x64UpdaterPath,
            arm64UpdaterPath,
            publicKeyPath,
            privateKeyPath,
            outputDirectory);
        Assert.True(
            result.ExitCode == 0,
            $"Manifest generator failed.{Environment.NewLine}{result.StandardError}");

        var manifest = await File.ReadAllBytesAsync(
            Path.Combine(outputDirectory, "AgentDesk-updater-manifest.json"));
        var signature = await File.ReadAllBytesAsync(
            Path.Combine(outputDirectory, "AgentDesk-updater-manifest.json.sig"));
        var handler = new SequenceHttpHandler(
            Response(manifest),
            Response(signature),
            Response(await File.ReadAllBytesAsync(x64UpdaterPath)));
        using var downloader = new SecureUpdateDownloader(handler, UpdateOriginPolicy.GitHub);
        using var service = new PortableUpdateService(downloader, new SafeZipExtractor());

        var staged = await service.CheckAndStageAsync(new UpdateCheckRequest(
            new Uri("https://github.com/rkshadow999/AgentDesk/releases/download/v1.2.3/AgentDesk-updater-manifest.json"),
            new Uri("https://github.com/rkshadow999/AgentDesk/releases/download/v1.2.3/AgentDesk-updater-manifest.json.sig"),
            key.ExportSubjectPublicKeyInfo(),
            SemanticVersion.Parse("1.0.0"),
            UpdateArchitecture.X64,
            AllowPrerelease: false,
            Path.Combine(_directory, "state"),
            ExpectedProduct: "AgentDesk.Updater"));

        Assert.NotNull(staged);
        Assert.Equal("AgentDesk.Updater", staged.Manifest.Product);
        Assert.EndsWith("-updater.zip", staged.Asset.Uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("AgentDesk.Updater.exe", staged.Asset.EntryPoint);
        Assert.Equal(
            x64Updater,
            await File.ReadAllBytesAsync(
                Path.Combine(staged.PayloadDirectory, "AgentDesk.Updater.exe")));
    }

    private static async Task<ProcessResult> RunGeneratorAsync(
        string generatorPath,
        string version,
        string x64PackagePath,
        string arm64PackagePath,
        string x64UpdaterPath,
        string arm64UpdaterPath,
        string publicKeyPath,
        string privateKeyPath,
        string outputDirectory)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-NoLogo",
            "-NoProfile",
            "-File",
            generatorPath,
            "-Version",
            version,
            "-Repository",
            "rkshadow999/AgentDesk",
            "-Tag",
            $"v{version}",
            "-X64PackagePath",
            x64PackagePath,
            "-Arm64PackagePath",
            arm64PackagePath,
            "-X64UpdaterPath",
            x64UpdaterPath,
            "-Arm64UpdaterPath",
            arm64UpdaterPath,
            "-PublicKeyPath",
            publicKeyPath,
            "-PrivateKeyPath",
            privateKeyPath,
            "-OutputDirectory",
            outputDirectory,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "scripts",
                    "agentdesk",
                    "New-AgentDeskUpdateManifest.ps1")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the AgentDesk repository root.");
    }

    private static void CreateArchive(string path, string entryName, byte[] content)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        entryStream.Write(content);
    }

    private static byte[] CreateMinimalPe(ushort machine)
    {
        var bytes = new byte[512];
        bytes[0] = 0x4d;
        bytes[1] = 0x5a;
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = 0x50;
        bytes[0x81] = 0x45;
        BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class SequenceHttpHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_responses.Dequeue());
    }
}
