using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentDesk.Cloud.Client;

public sealed class CloudConnectionProfile
{
    public CloudConnectionProfile()
    {
        IsLocalOnly = true;
    }

    public CloudConnectionProfile(Uri baseUri, string teamId, string deviceId)
    {
        var options = new CloudConnectionOptions(baseUri);
        BaseUri = options.BaseUri;
        TeamId = ValidateIdentifier(teamId, nameof(teamId));
        DeviceId = ValidateIdentifier(deviceId, nameof(deviceId));
        AccessTokenCredentialName = BuildCredentialName(BaseUri, TeamId, DeviceId);
    }

    public bool IsLocalOnly { get; }

    public Uri? BaseUri { get; }

    public string? TeamId { get; }

    public string? DeviceId { get; }

    [JsonIgnore]
    public string? AccessTokenCredentialName { get; }

    public CloudConnectionOptions CreateConnectionOptions()
    {
        if (BaseUri is null)
        {
            throw new InvalidOperationException("The local-only profile has no cloud endpoint.");
        }
        return new CloudConnectionOptions(BaseUri);
    }

    public override string ToString() => IsLocalOnly
        ? "CloudConnectionProfile { Mode = LocalOnly }"
        : $"CloudConnectionProfile {{ Mode = Remote, BaseUri = {BaseUri} }}";

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("The cloud profile identifier is invalid.", parameterName);
        }
        return value;
    }

    private static string BuildCredentialName(Uri baseUri, string teamId, string deviceId)
    {
        var endpoint = Encoding.UTF8.GetBytes(baseUri.AbsoluteUri);
        var team = Encoding.UTF8.GetBytes(teamId);
        var device = Encoding.UTF8.GetBytes(deviceId);
        var material = new byte[
            sizeof(int) * 3 + endpoint.Length + team.Length + device.Length];
        var destination = material.AsSpan();
        var offset = WriteLengthPrefixed(endpoint, destination, 0);
        offset = WriteLengthPrefixed(team, destination, offset);
        _ = WriteLengthPrefixed(device, destination, offset);
        var digest = SHA256.HashData(material);
        CryptographicOperations.ZeroMemory(material);
        return $"cloud/access/{Convert.ToHexStringLower(digest)}";
    }

    private static int WriteLengthPrefixed(byte[] value, Span<byte> destination, int offset)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], value.Length);
        offset += sizeof(int);
        value.CopyTo(destination[offset..]);
        return offset + value.Length;
    }
}
