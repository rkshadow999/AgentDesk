using System.Runtime.InteropServices;
using AgentDesk.Engine.Sidecar;

namespace AgentDesk.Engine.Tests;

public sealed class WslEngineInstallationVerifierTests
{
    [Theory]
    [InlineData(Architecture.X64, 0x3e, true)]
    [InlineData(Architecture.X64, 0xb7, false)]
    [InlineData(Architecture.Arm64, 0xb7, true)]
    [InlineData(Architecture.Arm64, 0x3e, false)]
    public void IsCompatibleElfRequiresTheCurrentProcessArchitecture(
        Architecture architecture,
        ushort machine,
        bool expected)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-elf-{Guid.NewGuid():N}");
        try
        {
            var header = new byte[20];
            header[0] = 0x7f;
            header[1] = (byte)'E';
            header[2] = (byte)'L';
            header[3] = (byte)'F';
            header[4] = 2;
            header[5] = 1;
            BitConverter.GetBytes((ushort)3).CopyTo(header, 16);
            BitConverter.GetBytes(machine).CopyTo(header, 18);
            File.WriteAllBytes(path, header);

            Assert.Equal(
                expected,
                SystemWslEngineInstallationVerifier.IsCompatibleElf(
                    path,
                    architecture));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsCompatibleElfRejectsTruncatedOrNonElfPayloads()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"agentdesk-elf-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(path, [0x7f, (byte)'E', (byte)'L']);

            Assert.False(SystemWslEngineInstallationVerifier.IsCompatibleElf(
                path,
                Architecture.X64));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SystemVerifierAcceptsTheOptInInstalledPayload()
    {
        var distribution = Environment.GetEnvironmentVariable(
            "AGENTDESK_EXPECTED_WSL_DISTRIBUTION");
        var sourcePath = Environment.GetEnvironmentVariable(
            "AGENTDESK_EXPECTED_WSL_ENGINE_SOURCE");
        if (string.IsNullOrWhiteSpace(distribution) ||
            string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        Assert.True(SystemWslEngineInstallationVerifier.Instance.IsCurrent(
            Path.Combine(Environment.SystemDirectory, "wsl.exe"),
            distribution,
            sourcePath));
    }
}
