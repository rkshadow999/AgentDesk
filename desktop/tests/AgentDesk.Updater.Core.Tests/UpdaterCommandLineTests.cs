using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class UpdaterCommandLineTests
{
    [Fact]
    public void ParseMapsACompleteApplyCommandWithoutJoiningRestartArguments()
    {
        var command = Assert.IsType<ApplyUpdateCommand>(UpdaterCommandLine.Parse(
        [
            "apply",
            "--manifest-uri", "https://github.com/example/manifest.json",
            "--signature-uri", "https://github.com/example/manifest.json.sig",
            "--public-key-file", "C:\\trusted\\update-key.spki",
            "--current-version", "1.2.3",
            "--architecture", "arm64",
            "--state-directory", "C:\\state",
            "--install-directory", "C:\\AgentDesk",
            "--parent-process-id", "42",
            "--parent-exit-timeout-seconds", "90",
            "--allow-prerelease",
            "--restart-argument", "--message",
            "--restart-argument", "hello world & calc.exe",
        ]));

        Assert.Equal(UpdateArchitecture.Arm64, command.Architecture);
        Assert.True(command.AllowPrerelease);
        Assert.Equal(42, command.ParentProcessId);
        Assert.Equal(TimeSpan.FromSeconds(90), command.ParentExitTimeout);
        Assert.Equal(["--message", "hello world & calc.exe"], command.RestartArguments);
    }

    [Fact]
    public void ParseMapsTheRecoveryCommand()
    {
        var command = Assert.IsType<RecoverUpdateCommand>(UpdaterCommandLine.Parse(
        [
            "recover",
            "--state-directory", "C:\\state",
            "--install-directory", "C:\\AgentDesk",
        ]));

        Assert.Equal("C:\\state", command.StateDirectory);
        Assert.Equal("C:\\AgentDesk", command.InstallationDirectory);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void ParseRejectsMissingDuplicateUnknownOrMalformedArguments(string[] arguments)
    {
        Assert.Throws<UpdaterCommandLineException>(() => UpdaterCommandLine.Parse(arguments));
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),
        new[] { "unknown" },
        new[] { "recover", "--state-directory", "state" },
        new[] { "recover", "--state-directory", "state", "--install-directory", "app", "--extra" },
        new[] { "apply", "--manifest-uri" },
        CompleteApply("--architecture", "x86"),
        CompleteApply("--current-version", "v1.2.3"),
        CompleteApply("--parent-process-id", "0"),
        CompleteApply("--parent-exit-timeout-seconds", "0"),
        CompleteApply("--manifest-uri", "http://github.com/example/manifest.json"),
        CompleteApply("--allow-prerelease", "--allow-prerelease"),
        CompleteApply("--state-directory", "one", "--state-directory", "two"),
    };

    private static string[] CompleteApply(params string[] replacement)
    {
        var values = new List<string>
        {
            "apply",
            "--manifest-uri", "https://github.com/example/manifest.json",
            "--signature-uri", "https://github.com/example/manifest.json.sig",
            "--public-key-file", "key.spki",
            "--current-version", "1.2.3",
            "--architecture", "x64",
            "--state-directory", "state",
            "--install-directory", "app",
        };
        for (var index = 0; index < replacement.Length; index += 2)
        {
            var option = replacement[index];
            var value = replacement[index + 1];
            var existing = values.IndexOf(option);
            if (existing >= 0 && option != "--allow-prerelease" && replacement.Count(item => item == option) == 1)
            {
                values[existing + 1] = value;
            }
            else
            {
                values.Add(option);
                if (value != option)
                {
                    values.Add(value);
                }
                else
                {
                    values.Add(value);
                }
            }
        }

        return values.ToArray();
    }
}
