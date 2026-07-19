namespace AgentDesk.Updater.Core;

public abstract record UpdaterCommand;

public sealed record ApplyUpdateCommand(
    Uri ManifestUri,
    Uri SignatureUri,
    string PublicKeyFile,
    SemanticVersion CurrentVersion,
    UpdateArchitecture Architecture,
    string StateDirectory,
    string InstallationDirectory,
    int? ParentProcessId,
    TimeSpan ParentExitTimeout,
    bool AllowPrerelease,
    IReadOnlyList<string> RestartArguments) : UpdaterCommand;

public sealed record RecoverUpdateCommand(
    string StateDirectory,
    string InstallationDirectory) : UpdaterCommand;

public sealed class UpdaterCommandLineException : Exception
{
    public UpdaterCommandLineException()
    {
    }

    public UpdaterCommandLineException(string message)
        : base(message)
    {
    }

    public UpdaterCommandLineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class UpdaterCommandLine
{
    private const int MaximumArguments = 300;
    private const int MaximumArgumentLength = 8192;

    public static UpdaterCommand Parse(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length is 0 or > MaximumArguments ||
            arguments.Any(argument =>
                argument is null ||
                argument.Length > MaximumArgumentLength ||
                argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new UpdaterCommandLineException("The updater command line is invalid.");
        }

        return arguments[0] switch
        {
            "apply" => ParseApply(arguments),
            "recover" => ParseRecover(arguments),
            _ => throw new UpdaterCommandLineException("The updater command is unknown."),
        };
    }

    private static ApplyUpdateCommand ParseApply(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var restartArguments = new List<string>();
        var allowPrerelease = false;
        for (var index = 1; index < arguments.Length; index++)
        {
            var option = arguments[index];
            if (option == "--allow-prerelease")
            {
                if (allowPrerelease)
                {
                    throw new UpdaterCommandLineException("An updater option was specified more than once.");
                }

                allowPrerelease = true;
                continue;
            }

            if (!IsApplyValueOption(option) || index + 1 >= arguments.Length)
            {
                throw new UpdaterCommandLineException("The updater apply options are invalid.");
            }

            var value = arguments[++index];
            if (option == "--restart-argument")
            {
                restartArguments.Add(value);
            }
            else if (!values.TryAdd(option, value))
            {
                throw new UpdaterCommandLineException("An updater option was specified more than once.");
            }
        }

        var manifestUri = ParseTrustedUri(Required(values, "--manifest-uri"));
        var signatureUri = ParseTrustedUri(Required(values, "--signature-uri"));
        SemanticVersion currentVersion;
        try
        {
            currentVersion = SemanticVersion.Parse(Required(values, "--current-version"));
        }
        catch (FormatException exception)
        {
            throw new UpdaterCommandLineException("The current version is not valid SemVer.", exception);
        }

        var architecture = Required(values, "--architecture") switch
        {
            "x64" => UpdateArchitecture.X64,
            "arm64" => UpdateArchitecture.Arm64,
            _ => throw new UpdaterCommandLineException("The updater architecture is unsupported."),
        };
        int? parentProcessId = values.TryGetValue("--parent-process-id", out var processIdText)
            ? ParsePositiveInt(processIdText, "parent process ID")
            : null;
        var timeoutSeconds = values.TryGetValue("--parent-exit-timeout-seconds", out var timeoutText)
            ? ParsePositiveInt(timeoutText, "parent exit timeout")
            : 120;
        if (timeoutSeconds > 86_400)
        {
            throw new UpdaterCommandLineException("The parent exit timeout is outside its permitted range.");
        }

        return new ApplyUpdateCommand(
            manifestUri,
            signatureUri,
            NonEmpty(Required(values, "--public-key-file"), "public key file"),
            currentVersion,
            architecture,
            NonEmpty(Required(values, "--state-directory"), "state directory"),
            NonEmpty(Required(values, "--install-directory"), "installation directory"),
            parentProcessId,
            TimeSpan.FromSeconds(timeoutSeconds),
            allowPrerelease,
            restartArguments);
    }

    private static RecoverUpdateCommand ParseRecover(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Length; index++)
        {
            var option = arguments[index];
            if (option is not "--state-directory" and not "--install-directory" ||
                index + 1 >= arguments.Length ||
                !values.TryAdd(option, arguments[++index]))
            {
                throw new UpdaterCommandLineException("The updater recovery options are invalid.");
            }
        }

        return new RecoverUpdateCommand(
            NonEmpty(Required(values, "--state-directory"), "state directory"),
            NonEmpty(Required(values, "--install-directory"), "installation directory"));
    }

    private static bool IsApplyValueOption(string option) => option is
        "--manifest-uri" or
        "--signature-uri" or
        "--public-key-file" or
        "--current-version" or
        "--architecture" or
        "--state-directory" or
        "--install-directory" or
        "--parent-process-id" or
        "--parent-exit-timeout-seconds" or
        "--restart-argument";

    private static string Required(IReadOnlyDictionary<string, string> values, string option) =>
        values.TryGetValue(option, out var value)
            ? value
            : throw new UpdaterCommandLineException($"The required updater option {option} is missing.");

    private static string NonEmpty(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UpdaterCommandLineException($"The updater {description} is empty.");
        }

        return value;
    }

    private static int ParsePositiveInt(string value, string description)
    {
        if (!int.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var result) ||
            result <= 0)
        {
            throw new UpdaterCommandLineException($"The updater {description} is invalid.");
        }

        return result;
    }

    private static Uri ParseTrustedUri(string value)
    {
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                throw new UpdaterCommandLineException("The updater URI is invalid.");
            }

            UpdateOriginPolicy.GitHub.EnsureAllowedInitialUri(uri);
            return uri;
        }
        catch (UpdateSecurityException exception)
        {
            throw new UpdaterCommandLineException("The updater URI is not trusted.", exception);
        }
    }
}
