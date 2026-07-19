using System.Text.RegularExpressions;

namespace AgentDesk.Updater.Core;

public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private const int MaximumLength = 256;
    private static readonly Regex Pattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" +
        "(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)" +
        "(?:\\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?" +
        "(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly string _major;
    private readonly string _minor;
    private readonly string _patch;
    private readonly string? _prerelease;
    private readonly string? _build;

    private SemanticVersion(
        string major,
        string minor,
        string patch,
        string? prerelease,
        string? build)
    {
        _major = major;
        _minor = minor;
        _patch = patch;
        _prerelease = prerelease;
        _build = build;
    }

    public bool IsPrerelease => _prerelease is not null;

    public static SemanticVersion Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaximumLength)
        {
            throw new FormatException("The semantic version is invalid.");
        }

        var match = Pattern.Match(value);
        if (!match.Success)
        {
            throw new FormatException("The semantic version is invalid.");
        }

        return new SemanticVersion(
            match.Groups[1].Value,
            match.Groups[2].Value,
            match.Groups[3].Value,
            match.Groups[4].Success ? match.Groups[4].Value : null,
            match.Groups[5].Success ? match.Groups[5].Value : null);
    }

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        if (value is null)
        {
            version = default;
            return false;
        }

        try
        {
            version = Parse(value);
            return true;
        }
        catch (FormatException)
        {
            version = default;
            return false;
        }
    }

    public int CompareTo(SemanticVersion other) => CompareCore(other, useAgentDeskChannels: false);

    internal int CompareAgentDeskReleaseTo(SemanticVersion other) =>
        CompareCore(other, useAgentDeskChannels: true);

    private int CompareCore(SemanticVersion other, bool useAgentDeskChannels)
    {
        var comparison = CompareNumericIdentifier(_major, other._major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNumericIdentifier(_minor, other._minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNumericIdentifier(_patch, other._patch);
        if (comparison != 0)
        {
            return comparison;
        }

        if (_prerelease is null)
        {
            return other._prerelease is null ? 0 : 1;
        }

        if (other._prerelease is null)
        {
            return -1;
        }

        if (useAgentDeskChannels)
        {
            var leftIsAgentDesk = TryGetAgentDeskPrerelease(
                out var leftChannel,
                out var leftSequence);
            var rightIsAgentDesk = other.TryGetAgentDeskPrerelease(
                out var rightChannel,
                out var rightSequence);
            if (leftIsAgentDesk && rightIsAgentDesk)
            {
                comparison = leftChannel.CompareTo(rightChannel);
                return comparison != 0
                    ? comparison
                    : CompareNumericIdentifier(leftSequence, rightSequence);
            }

            if (leftIsAgentDesk != rightIsAgentDesk)
            {
                return leftIsAgentDesk ? -1 : 1;
            }
        }

        var left = _prerelease.Split('.');
        var right = other._prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = IsNumeric(left[index]);
            var rightNumeric = IsNumeric(right[index]);
            if (leftNumeric && rightNumeric)
            {
                comparison = CompareNumericIdentifier(left[index], right[index]);
            }
            else if (leftNumeric != rightNumeric)
            {
                comparison = leftNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(left[index], right[index]);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(SemanticVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_major, _minor, _patch, _prerelease);

    public override string ToString()
    {
        var result = $"{_major}.{_minor}.{_patch}";
        if (_prerelease is not null)
        {
            result += $"-{_prerelease}";
        }

        if (_build is not null)
        {
            result += $"+{_build}";
        }

        return result;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

    public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

    private static int CompareNumericIdentifier(string left, string right) =>
        left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right);

    private static bool IsNumeric(string value) => value.All(character => character is >= '0' and <= '9');

    private bool TryGetAgentDeskPrerelease(out int channel, out string sequence)
    {
        var identifiers = _prerelease?.Split('.');
        if (identifiers is not { Length: 2 } || !IsNumeric(identifiers[1]))
        {
            channel = default;
            sequence = string.Empty;
            return false;
        }

        channel = identifiers[0] switch
        {
            "ci" => 0,
            "alpha" => 1,
            "beta" => 2,
            "preview" => 3,
            "rc" => 4,
            _ => -1,
        };
        sequence = identifiers[1];
        return channel >= 0;
    }
}
