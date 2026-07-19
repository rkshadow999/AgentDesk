using System.Security.Cryptography;
using System.Text;
using AgentDesk.Updater.Core;

namespace AgentDesk.Updater.Core.Tests;

public sealed class ManifestVerifierTests
{
    [Fact]
    public void VerifyAuthenticatesTheExactManifestBytesAndMapsAssets()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Manifest("1.2.3");
        var signature = Sign(key, bytes);

        var manifest = UpdateManifestVerifier.Verify(
            bytes,
            signature,
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub);

        Assert.Equal(SemanticVersion.Parse("1.2.3"), manifest.Version);
        Assert.Equal("AgentDesk", manifest.Product);
        Assert.Collection(
            manifest.Assets,
            asset =>
            {
                Assert.Equal(UpdateArchitecture.X64, asset.Architecture);
                Assert.Equal(64, asset.Sha256Hex.Length);
                Assert.Equal(1234, asset.Size);
                Assert.Equal("AgentDesk.exe", asset.EntryPoint);
            });
    }

    [Fact]
    public void VerifyAcceptsTheUpdaterManifestOnlyWhenExplicitlyExpected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Encoding.UTF8.GetBytes(
            """
            {
              "schemaVersion":1,
              "product":"AgentDesk.Updater",
              "version":"1.2.3",
              "assets":[{
                "architecture":"x64",
                "url":"https://github.com/rkshadow999/AgentDesk/releases/download/v1.2.3/AgentDesk-updater.exe",
                "sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "size":1234,
                "entryPoint":"AgentDesk.Updater.exe"
              }]
            }
            """);
        var signature = Sign(key, bytes);

        Assert.Throws<UpdateSecurityException>(() => UpdateManifestVerifier.Verify(
            bytes,
            signature,
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub));

        var manifest = UpdateManifestVerifier.Verify(
            bytes,
            signature,
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub,
            expectedProduct: "AgentDesk.Updater");

        Assert.Equal("AgentDesk.Updater", manifest.Product);
        Assert.Equal("AgentDesk.Updater.exe", Assert.Single(manifest.Assets).EntryPoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AgentDesk.Beta")]
    [InlineData("agentdesk")]
    public void VerifyRejectsUnsupportedExpectedProductNames(string expectedProduct)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Manifest("1.2.3");

        Assert.Throws<ArgumentException>(() => UpdateManifestVerifier.Verify(
            bytes,
            Sign(key, bytes),
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub,
            expectedProduct));
    }

    [Fact]
    public void VerifyRejectsAnyPostSignatureManifestMutation()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = Manifest("1.2.3");
        var signature = Sign(key, signed);
        var tampered = Manifest("9.9.9");

        Assert.Throws<UpdateSecurityException>(() => UpdateManifestVerifier.Verify(
            tampered,
            signature,
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub));
    }

    [Fact]
    public void VerifyRejectsSignaturesFromAnotherKey()
    {
        using var trusted = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Manifest("1.2.3");

        Assert.Throws<UpdateSecurityException>(() => UpdateManifestVerifier.Verify(
            bytes,
            Sign(attacker, bytes),
            trusted.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub));
    }

    [Fact]
    public void VerifyRejectsKeysThatAreNotP256()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var bytes = Manifest("1.2.3");

        Assert.Throws<UpdateSecurityException>(() => UpdateManifestVerifier.Verify(
            bytes,
            Sign(key, bytes),
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub));
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void VerifyRejectsAmbiguousOrUnsafeManifestData(string json)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Encoding.UTF8.GetBytes(json);

        Assert.Throws<UpdateSecurityException>(() => UpdateManifestVerifier.Verify(
            bytes,
            Sign(key, bytes),
            key.ExportSubjectPublicKeyInfo(),
            UpdateOriginPolicy.GitHub));
    }

    public static TheoryData<string> InvalidManifests => new()
    {
        ManifestJson("\"schemaVersion\":1,\"schemaVersion\":1"),
        ManifestJson("\"schemaVersion\":2"),
        ManifestJson("\"schemaVersion\":1", product: "OtherProduct"),
        ManifestJson("\"schemaVersion\":1", sha256: "00"),
        ManifestJson("\"schemaVersion\":1", size: 0),
        ManifestJson("\"schemaVersion\":1", size: 536_870_913),
        ManifestJson("\"schemaVersion\":1", architecture: "x86"),
        ManifestJson("\"schemaVersion\":1", url: "http://github.com/example/AgentDesk.zip"),
        ManifestJson("\"schemaVersion\":1", url: "https://evil.example/AgentDesk.zip"),
        ManifestJson("\"schemaVersion\":1", url: "https://user:pass@github.com/example/AgentDesk.zip"),
        ManifestJson("\"schemaVersion\":1", entryPoint: "../AgentDesk.exe"),
        ManifestJson("\"schemaVersion\":1", entryPoint: "AgentDesk.cmd"),
        ManifestJson("\"schemaVersion\":1", extraAssetProperty: ",\"unexpected\":true"),
        ManifestJson("\"schemaVersion\":1", extraRootProperty: ",\"unexpected\":true"),
    };

    internal static byte[] Manifest(string version) => Encoding.UTF8.GetBytes(ManifestJson(
        "\"schemaVersion\":1",
        version: version));

    internal static byte[] Sign(ECDsa key, byte[] bytes) => key.SignData(
        bytes,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.Rfc3279DerSequence);

    private static string ManifestJson(
        string schema,
        string product = "AgentDesk",
        string version = "1.2.3",
        string architecture = "x64",
        string url = "https://github.com/rkshadow999/AgentDesk/releases/download/v1.2.3/AgentDesk.zip",
        string sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        long size = 1234,
        string entryPoint = "AgentDesk.exe",
        string extraAssetProperty = "",
        string extraRootProperty = "") => $$"""
        {
          {{schema}},
          "product":"{{product}}",
          "version":"{{version}}",
          "assets":[{
            "architecture":"{{architecture}}",
            "url":"{{url}}",
            "sha256":"{{sha256}}",
            "size":{{size}},
            "entryPoint":"{{entryPoint}}"{{extraAssetProperty}}
          }]{{extraRootProperty}}
        }
        """;
}
