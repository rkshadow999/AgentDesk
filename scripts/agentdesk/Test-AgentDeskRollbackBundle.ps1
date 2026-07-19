<#
.SYNOPSIS
Runs focused regression tests for the AgentDesk previous-release rollback bundle.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bundler = Join-Path $PSScriptRoot "New-AgentDeskRollbackBundle.ps1"
if (-not (Test-Path -LiteralPath $bundler -PathType Leaf)) {
    throw "Rollback bundler is missing: $bundler"
}
$signatureVerifier = Join-Path $PSScriptRoot "Verify-AgentDeskMsixSignature.ps1"
if (-not (Test-Path -LiteralPath $signatureVerifier -PathType Leaf)) {
    throw "MSIX signature verifier is missing: $signatureVerifier"
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Write-RollbackFixture {
    param(
        [Parameter(Mandatory)][string]$Root,
        [string]$Version = "0.1.0",
        [Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$UpdateSigningKey
    )

    if (Test-Path -LiteralPath $Root) {
        Remove-Item -LiteralPath $Root -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($Root) | Out-Null

    foreach ($architecture in @("x64", "arm64")) {
        $prefix = "AgentDesk-$Version-win-$architecture"
        foreach ($name in @(
            "$prefix-portable.zip",
            "$prefix-updater.zip",
            "$prefix-UPDATE-STATUS.txt",
            "$prefix.spdx.json",
            "$prefix.cyclonedx.json"
        )) {
            Write-Utf8NoBom -Path (Join-Path $Root $name) -Content "fixture:$name"
        }
        Write-MsixIdentityFixture `
            -Path (Join-Path $Root "$prefix.msix") `
            -Publisher "CN=AgentDesk" `
            -PackageVersion "$Version.65535" `
            -Architecture $architecture
        Write-Utf8NoBom `
            -Path (Join-Path $Root "$prefix-MSIX-SIGNING-STATUS.txt") `
            -Content "signed`n"

        $checksumLines = Get-ChildItem -LiteralPath $Root -File -Filter "$prefix*" |
            Where-Object Name -NotLike "*-SHA256SUMS.txt" |
            Sort-Object Name |
            ForEach-Object {
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $($_.Name)"
            }
        Write-Utf8NoBom `
            -Path (Join-Path $Root "$prefix-SHA256SUMS.txt") `
            -Content (($checksumLines -join "`n") + "`n")
    }

    Write-SignedUpdateManifestFixture `
        -Root $Root `
        -Version $Version `
        -SigningKey $UpdateSigningKey
}

function Write-SignedUpdateManifestFixture {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Version,
        [Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$SigningKey,
        [string]$Product = "AgentDesk",
        [string]$ManifestVersion,
        [string]$X64Architecture,
        [string]$X64Url,
        [string]$X64Sha256,
        [long]$X64Size = -1,
        [string]$X64EntryPoint
    )

    $effectiveManifestVersion = if ([string]::IsNullOrWhiteSpace($ManifestVersion)) {
        $Version
    }
    else {
        $ManifestVersion
    }
    $memory = [System.IO.MemoryStream]::new()
    $writer = $null
    try {
        $options = [System.Text.Json.JsonWriterOptions]::new()
        $writer = [System.Text.Json.Utf8JsonWriter]::new($memory, $options)
        $writer.WriteStartObject()
        $writer.WriteNumber("schemaVersion", 1)
        $writer.WriteString("product", $Product)
        $writer.WriteString("version", $effectiveManifestVersion)
        $writer.WriteStartArray("assets")
        foreach ($architecture in @("x64", "arm64")) {
            $name = "AgentDesk-$Version-win-$architecture-portable.zip"
            $path = Join-Path $Root $name
            $manifestArchitecture = if ($architecture -eq "x64" -and
                -not [string]::IsNullOrWhiteSpace($X64Architecture)) {
                $X64Architecture
            }
            else {
                $architecture
            }
            $url = if ($architecture -eq "x64" -and
                -not [string]::IsNullOrWhiteSpace($X64Url)) {
                $X64Url
            }
            else {
                "https://github.com/rkshadow999/AgentDesk/releases/download/v$Version/$name"
            }
            $sha256 = if ($architecture -eq "x64" -and
                -not [string]::IsNullOrWhiteSpace($X64Sha256)) {
                $X64Sha256
            }
            else {
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $size = if ($architecture -eq "x64" -and $X64Size -ge 0) {
                $X64Size
            }
            else {
                (Get-Item -LiteralPath $path).Length
            }
            $entryPoint = if ($architecture -eq "x64" -and
                -not [string]::IsNullOrWhiteSpace($X64EntryPoint)) {
                $X64EntryPoint
            }
            else {
                "AgentDesk.App.exe"
            }
            $writer.WriteStartObject()
            $writer.WriteString("architecture", $manifestArchitecture)
            $writer.WriteString("url", $url)
            $writer.WriteString("sha256", $sha256)
            $writer.WriteNumber("size", $size)
            $writer.WriteString("entryPoint", $entryPoint)
            $writer.WriteEndObject()
        }
        $writer.WriteEndArray()
        $writer.WriteEndObject()
        $writer.Flush()
        $manifestBytes = $memory.ToArray()
    }
    finally {
        if ($null -ne $writer) {
            $writer.Dispose()
        }
        $memory.Dispose()
    }

    [System.IO.File]::WriteAllBytes(
        (Join-Path $Root "AgentDesk-update-manifest.json"),
        $manifestBytes)
    $signature = $SigningKey.SignData(
        $manifestBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    [System.IO.File]::WriteAllBytes(
        (Join-Path $Root "AgentDesk-update-manifest.json.sig"),
        $signature)
}

function Write-MsixIdentityFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Publisher,
        [string]$PackageName = "AgentDesk",
        [string]$PackageVersion = "1.0.0.65535",
        [ValidateSet("x64", "arm64")][string]$Architecture = "x64"
    )

    $stageRoot = Join-Path ([System.IO.Path]::GetDirectoryName($Path)) (
        ".msix-fixture-" + [guid]::NewGuid().ToString("N"))
    try {
        [System.IO.Directory]::CreateDirectory($stageRoot) | Out-Null
        $escapedPublisher = [System.Security.SecurityElement]::Escape($Publisher)
        Write-Utf8NoBom `
            -Path (Join-Path $stageRoot "AppxManifest.xml") `
            -Content @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
  <Identity Name="$PackageName" Publisher="$escapedPublisher" Version="$PackageVersion" ProcessorArchitecture="$Architecture" />
</Package>
"@
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stageRoot, $Path)
    }
    finally {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-SignatureVerifierFixture {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$SignerSubject,
        [Parameter(Mandatory)][string]$SignerThumbprint,
        [Parameter(Mandatory)][string]$SignToolPath,
        [string]$ExpectedPackageName = "AgentDesk",
        [string]$ExpectedPackageVersion = "1.0.0.65535",
        [ValidateSet("x64", "arm64")][string]$ExpectedArchitecture = "x64",
        [string]$ExpectedThumbprint
    )

    $global:AgentDeskTestSignerSubject = $SignerSubject
    $global:AgentDeskTestSignerThumbprint = $SignerThumbprint
    $parameters = @{
        PackagePath = $PackagePath
        SignToolPath = $SignToolPath
        ExpectedPackageName = $ExpectedPackageName
        ExpectedPackageVersion = $ExpectedPackageVersion
        ExpectedArchitecture = $ExpectedArchitecture
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
        $parameters.ExpectedThumbprint = $ExpectedThumbprint
    }
    & $signatureVerifier @parameters
}

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessageFragment
    )

    try {
        & $Action
    }
    catch {
        if (-not $_.Exception.Message.Contains($MessageFragment)) {
            throw "Rollback bundle failed for the wrong reason: $($_.Exception.Message)"
        }
        return
    }
    throw "Expected rollback bundle creation to reject '$MessageFragment'."
}

function Invoke-PortableRollbackFixture {
    param(
        [Parameter(Mandatory)][string]$PreviousRoot,
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$SignaturePath,
        [Parameter(Mandatory)][string]$PublicKeyPath,
        [Parameter(Mandatory)][string]$PublicKeySha256
    )

    & $bundler `
        -CurrentVersion "0.2.0" `
        -CurrentTag "v0.2.0" `
        -PreviousVersion "0.1.0" `
        -PreviousTag "v0.1.0" `
        -PreviousReleaseRoot $PreviousRoot `
        -OutputRoot $OutputRoot `
        -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
        -PreviousUpdateManifestPath $ManifestPath `
        -PreviousUpdateManifestSignaturePath $SignaturePath `
        -TrustedUpdatePublicKeyPath $PublicKeyPath `
        -ExpectedPreviousUpdatePublicKeySha256 $PublicKeySha256 | Out-Null
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-rollback-test-" + [guid]::NewGuid().ToString("N"))
$previousRoot = Join-Path $fixtureRoot "previous"
$outputRoot = Join-Path $fixtureRoot "output"
$expandedRoot = Join-Path $fixtureRoot "expanded"
$updateSigningKey = $null

try {
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $updateSigningKey = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $trustedUpdatePublicKey = Join-Path $fixtureRoot "trusted-update-public-key.spki"
    [System.IO.File]::WriteAllBytes(
        $trustedUpdatePublicKey,
        $updateSigningKey.ExportSubjectPublicKeyInfo())
    $trustedUpdatePublicKeySha256 = (
        Get-FileHash -LiteralPath $trustedUpdatePublicKey -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $fakeSignTool = Join-Path $fixtureRoot "signtool.cmd"
    Write-Utf8NoBom -Path $fakeSignTool -Content @"
@echo off
if not "%AGENTDESK_TEST_MUTATE_PATH%"=="" (
  >"%AGENTDESK_TEST_MUTATE_PATH%" echo tampered-after-staging
)
exit /b 0
"@
    function global:Get-AuthenticodeSignature {
        param([Parameter(Mandatory)][string]$LiteralPath)

        return [pscustomobject]@{
            Status = [System.Management.Automation.SignatureStatus]::Valid
            StatusMessage = "Valid fixture signature"
            SignerCertificate = [pscustomobject]@{
                Subject = $global:AgentDeskTestSignerSubject
                Thumbprint = $global:AgentDeskTestSignerThumbprint
            }
        }
    }

    $trustedThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567"
    $trustedMsix = Join-Path $fixtureRoot "trusted.msix"
    Write-MsixIdentityFixture -Path $trustedMsix -Publisher "CN=AgentDesk"
    Invoke-SignatureVerifierFixture `
        -PackagePath $trustedMsix `
        -SignerSubject "CN=AgentDesk" `
        -SignerThumbprint $trustedThumbprint `
        -SignToolPath $fakeSignTool `
        -ExpectedThumbprint $trustedThumbprint

    foreach ($identityMismatch in @(
        @{
            Name = "OtherProduct"
            Version = "1.0.0.65535"
            Architecture = "x64"
            Message = "package Name"
        },
        @{
            Name = "AgentDesk"
            Version = "9.9.9.65535"
            Architecture = "x64"
            Message = "package Version"
        },
        @{
            Name = "AgentDesk"
            Version = "1.0.0.65535"
            Architecture = "arm64"
            Message = "package architecture"
        }
    )) {
        $mismatchedMsix = Join-Path $fixtureRoot (
            "identity-mismatch-" + [guid]::NewGuid().ToString("N") + ".msix")
        Write-MsixIdentityFixture `
            -Path $mismatchedMsix `
            -Publisher "CN=AgentDesk" `
            -PackageName $identityMismatch.Name `
            -PackageVersion $identityMismatch.Version `
            -Architecture $identityMismatch.Architecture
        Invoke-ExpectedFailure -MessageFragment $identityMismatch.Message -Action {
            Invoke-SignatureVerifierFixture `
                -PackagePath $mismatchedMsix `
                -SignerSubject "CN=AgentDesk" `
                -SignerThumbprint $trustedThumbprint `
                -SignToolPath $fakeSignTool `
                -ExpectedThumbprint $trustedThumbprint
        }
    }

    $stableSourceMsix = Join-Path $fixtureRoot "stable-source.msix"
    Write-MsixIdentityFixture -Path $stableSourceMsix -Publisher "CN=AgentDesk"
    try {
        [Environment]::SetEnvironmentVariable(
            "AGENTDESK_TEST_MUTATE_PATH",
            $stableSourceMsix,
            [EnvironmentVariableTarget]::Process)
        Invoke-SignatureVerifierFixture `
            -PackagePath $stableSourceMsix `
            -SignerSubject "CN=AgentDesk" `
            -SignerThumbprint $trustedThumbprint `
            -SignToolPath $fakeSignTool `
            -ExpectedThumbprint $trustedThumbprint
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            "AGENTDESK_TEST_MUTATE_PATH",
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    if ((Get-Content -LiteralPath $stableSourceMsix -Raw).Trim() -cne
        "tampered-after-staging") {
        throw "The TOCTOU fixture did not replace the original MSIX after staging began."
    }

    $selfSignedMsix = Join-Path $fixtureRoot "self-signed-other-publisher.msix"
    Write-MsixIdentityFixture -Path $selfSignedMsix -Publisher "CN=OtherPublisher"
    Invoke-ExpectedFailure -MessageFragment "trusted Publisher" -Action {
        Invoke-SignatureVerifierFixture `
            -PackagePath $selfSignedMsix `
            -SignerSubject "CN=OtherPublisher" `
            -SignerThumbprint "1111111111111111111111111111111111111111" `
            -SignToolPath $fakeSignTool
    }

    $otherTrustedSignerMsix = Join-Path $fixtureRoot "other-trusted-signer.msix"
    Write-MsixIdentityFixture -Path $otherTrustedSignerMsix -Publisher "CN=AgentDesk"
    Invoke-ExpectedFailure -MessageFragment "trusted Publisher" -Action {
        Invoke-SignatureVerifierFixture `
            -PackagePath $otherTrustedSignerMsix `
            -SignerSubject "CN=Contoso Trusted Signing" `
            -SignerThumbprint "2222222222222222222222222222222222222222" `
            -SignToolPath $fakeSignTool
    }

    Invoke-ExpectedFailure -MessageFragment "thumbprint" -Action {
        Invoke-SignatureVerifierFixture `
            -PackagePath $trustedMsix `
            -SignerSubject "CN=AgentDesk" `
            -SignerThumbprint $trustedThumbprint `
            -SignToolPath $fakeSignTool `
            -ExpectedThumbprint "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $previousUpdateManifest = Join-Path $previousRoot "AgentDesk-update-manifest.json"
    $previousUpdateManifestSignature = Join-Path $previousRoot "AgentDesk-update-manifest.json.sig"
    foreach ($invalidVersionPair in @(
        @{ Current = "0.2.0"; Previous = "0.2.0" },
        @{ Current = "0.2.0-rc.1"; Previous = "0.2.0" },
        @{ Current = "0.2.0"; Previous = "0.3.0-alpha.1" }
    )) {
        Invoke-ExpectedFailure -MessageFragment "lower than CurrentVersion" -Action {
            & $bundler `
                -CurrentVersion $invalidVersionPair.Current `
                -CurrentTag "v$($invalidVersionPair.Current)" `
                -PreviousVersion $invalidVersionPair.Previous `
                -PreviousTag "v$($invalidVersionPair.Previous)" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" | Out-Null
        }
    }

    Invoke-ExpectedFailure `
        -MessageFragment "previous release MSIX signer thumbprint is required" `
        -Action {
            & $bundler `
                -CurrentVersion "0.2.0" `
                -CurrentTag "v0.2.0" `
                -PreviousVersion "0.1.0" `
                -PreviousTag "v0.1.0" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
                -PreviousUpdateManifestPath $previousUpdateManifest `
                -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
                -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
                -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 `
                -SignToolPath $fakeSignTool `
                -RequireSignedMsix | Out-Null
        }

    Invoke-ExpectedFailure `
        -MessageFragment "signed previous update manifest" `
        -Action {
            & $bundler `
                -CurrentVersion "0.2.0" `
                -CurrentTag "v0.2.0" `
                -PreviousVersion "0.1.0" `
                -PreviousTag "v0.1.0" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" | Out-Null
        }

    Invoke-ExpectedFailure `
        -MessageFragment "previous update public key SHA-256 pin is required" `
        -Action {
            & $bundler `
                -CurrentVersion "0.2.0" `
                -CurrentTag "v0.2.0" `
                -PreviousVersion "0.1.0" `
                -PreviousTag "v0.1.0" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
                -PreviousUpdateManifestPath $previousUpdateManifest `
                -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
                -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey | Out-Null
        }

    Invoke-ExpectedFailure `
        -MessageFragment "does not match the repository-pinned SHA-256" `
        -Action {
            & $bundler `
                -CurrentVersion "0.2.0" `
                -CurrentTag "v0.2.0" `
                -PreviousVersion "0.1.0" `
                -PreviousTag "v0.1.0" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
                -PreviousUpdateManifestPath $previousUpdateManifest `
                -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
                -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
                -ExpectedPreviousUpdatePublicKeySha256 ("0" * 64) | Out-Null
        }

    $tamperedSignature = [System.IO.File]::ReadAllBytes($previousUpdateManifestSignature)
    $tamperedSignature[0] = $tamperedSignature[0] -bxor 0xff
    [System.IO.File]::WriteAllBytes($previousUpdateManifestSignature, $tamperedSignature)
    Invoke-ExpectedFailure `
        -MessageFragment "previous update manifest signature is invalid" `
        -Action {
            & $bundler `
                -CurrentVersion "0.2.0" `
                -CurrentTag "v0.2.0" `
                -PreviousVersion "0.1.0" `
                -PreviousTag "v0.1.0" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
                -PreviousUpdateManifestPath $previousUpdateManifest `
                -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
                -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
                -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 | Out-Null
        }
    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey

    $x64Portable = Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-portable.zip"
    Write-Utf8NoBom -Path $x64Portable -Content "manifest-hash-from-different-bytes"
    Write-SignedUpdateManifestFixture `
        -Root $previousRoot `
        -Version "0.1.0" `
        -SigningKey $updateSigningKey
    Write-Utf8NoBom `
        -Path $x64Portable `
        -Content "fixture:AgentDesk-0.1.0-win-x64-portable.zip"
    Invoke-ExpectedFailure `
        -MessageFragment "does not match the verified Portable asset" `
        -Action {
            & $bundler `
                -CurrentVersion "0.2.0" `
                -CurrentTag "v0.2.0" `
                -PreviousVersion "0.1.0" `
                -PreviousTag "v0.1.0" `
                -PreviousReleaseRoot $previousRoot `
                -OutputRoot $outputRoot `
                -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
                -PreviousUpdateManifestPath $previousUpdateManifest `
                -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
                -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
                -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 | Out-Null
        }
    foreach ($manifestFailure in @(
        @{
            Message = "identity is invalid"
            Overrides = @{ Product = "OtherProduct" }
        },
        @{
            Message = "identity is invalid"
            Overrides = @{ ManifestVersion = "0.0.9" }
        },
        @{
            Message = "architecture set is invalid"
            Overrides = @{ X64Architecture = "x86" }
        },
        @{
            Message = "asset metadata is invalid"
            Overrides = @{ X64Url = "https://example.invalid/wrong.zip" }
        },
        @{
            Message = "asset metadata is invalid"
            Overrides = @{ X64Size = 0 }
        },
        @{
            Message = "asset metadata is invalid"
            Overrides = @{ X64EntryPoint = "Wrong.exe" }
        }
    )) {
        Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
        $manifestParameters = @{
            Root = $previousRoot
            Version = "0.1.0"
            SigningKey = $updateSigningKey
        }
        foreach ($overrideName in $manifestFailure.Overrides.Keys) {
            $manifestParameters[$overrideName] = $manifestFailure.Overrides[$overrideName]
        }
        Write-SignedUpdateManifestFixture @manifestParameters
        Invoke-ExpectedFailure -MessageFragment $manifestFailure.Message -Action {
            Invoke-PortableRollbackFixture `
                -PreviousRoot $previousRoot `
                -OutputRoot $outputRoot `
                -ManifestPath $previousUpdateManifest `
                -SignaturePath $previousUpdateManifestSignature `
                -PublicKeyPath $trustedUpdatePublicKey `
                -PublicKeySha256 $trustedUpdatePublicKeySha256
        }
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $manifestBytes = [System.IO.File]::ReadAllBytes($previousUpdateManifest)
    $nonDerSignature = $updateSigningKey.SignData(
        $manifestBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    [System.IO.File]::WriteAllBytes($previousUpdateManifestSignature, $nonDerSignature)
    Invoke-ExpectedFailure -MessageFragment "previous update manifest signature is invalid" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    $wrongCurveKey = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP384)
    try {
        Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $wrongCurveKey
        $wrongCurvePublicKey = Join-Path $fixtureRoot "wrong-curve-update-public-key.spki"
        [System.IO.File]::WriteAllBytes(
            $wrongCurvePublicKey,
            $wrongCurveKey.ExportSubjectPublicKeyInfo())
        $wrongCurvePublicKeySha256 = (
            Get-FileHash -LiteralPath $wrongCurvePublicKey -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        Invoke-ExpectedFailure -MessageFragment "must be ECDSA P-256" -Action {
            Invoke-PortableRollbackFixture `
                -PreviousRoot $previousRoot `
                -OutputRoot $outputRoot `
                -ManifestPath $previousUpdateManifest `
                -SignaturePath $previousUpdateManifestSignature `
                -PublicKeyPath $wrongCurvePublicKey `
                -PublicKeySha256 $wrongCurvePublicKeySha256
        }
    }
    finally {
        $wrongCurveKey.Dispose()
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    Add-Content `
        -LiteralPath (Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-updater.zip") `
        -Value "tampered"
    Invoke-ExpectedFailure -MessageFragment "SHA-256 mismatch" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $missingUpdateStatus = Join-Path $previousRoot "AgentDesk-0.1.0-win-arm64-UPDATE-STATUS.txt"
    Remove-Item -LiteralPath $missingUpdateStatus -Force
    Invoke-ExpectedFailure -MessageFragment "arm64-UPDATE-STATUS.txt' is missing" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $x64ChecksumPath = Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-SHA256SUMS.txt"
    $checksumWithoutUpdater = Get-Content -LiteralPath $x64ChecksumPath |
        Where-Object { $_ -notmatch '-updater\.zip$' }
    Write-Utf8NoBom `
        -Path $x64ChecksumPath `
        -Content (($checksumWithoutUpdater -join "`n") + "`n")
    Invoke-ExpectedFailure -MessageFragment "does not cover required asset" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $x64ChecksumPath = Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-SHA256SUMS.txt"
    Add-Content -LiteralPath $x64ChecksumPath -Value (("0" * 64) + "  unexpected.bin")
    Invoke-ExpectedFailure -MessageFragment "lists an unexpected asset" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $x64ChecksumPath = Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-SHA256SUMS.txt"
    $duplicateChecksumLine = Get-Content -LiteralPath $x64ChecksumPath | Select-Object -First 1
    Add-Content -LiteralPath $x64ChecksumPath -Value $duplicateChecksumLine
    Invoke-ExpectedFailure -MessageFragment "lists an asset more than once" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $x64ChecksumPath = Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-SHA256SUMS.txt"
    $caseVariantChecksum = (Get-Content -LiteralPath $x64ChecksumPath -Raw).Replace(
        "AgentDesk-0.1.0-win-x64-portable.zip",
        "AgentDesk-0.1.0-win-x64-PORTABLE.zip")
    Write-Utf8NoBom -Path $x64ChecksumPath -Content $caseVariantChecksum
    Invoke-ExpectedFailure -MessageFragment "lists an unexpected asset" -Action {
        Invoke-PortableRollbackFixture `
            -PreviousRoot $previousRoot `
            -OutputRoot $outputRoot `
            -ManifestPath $previousUpdateManifest `
            -SignaturePath $previousUpdateManifestSignature `
            -PublicKeyPath $trustedUpdatePublicKey `
            -PublicKeySha256 $trustedUpdatePublicKeySha256
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $mutationSourceAsset = Join-Path $previousRoot "AgentDesk-0.1.0-win-arm64-portable.zip"
    $stableMutationHash = (Get-FileHash -LiteralPath $mutationSourceAsset -Algorithm SHA256).Hash.ToLowerInvariant()
    try {
        [Environment]::SetEnvironmentVariable(
            "AGENTDESK_TEST_MUTATE_PATH",
            $mutationSourceAsset,
            [EnvironmentVariableTarget]::Process)
        $bundlePath = & $bundler `
            -CurrentVersion "0.2.0" `
            -CurrentTag "v0.2.0" `
            -PreviousVersion "0.1.0" `
            -PreviousTag "v0.1.0" `
            -PreviousReleaseRoot $previousRoot `
            -OutputRoot $outputRoot `
            -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
            -PreviousUpdateManifestPath $previousUpdateManifest `
            -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
            -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
            -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 `
            -ExpectedPreviousMsixSignerThumbprint $trustedThumbprint `
            -SignToolPath $fakeSignTool `
            -RequireSignedMsix
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            "AGENTDESK_TEST_MUTATE_PATH",
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    if ((Get-Content -LiteralPath $mutationSourceAsset -Raw).Trim() -cne
        "tampered-after-staging") {
        throw "The rollback TOCTOU fixture did not replace the original release asset."
    }

    $expectedBundle = Join-Path $outputRoot "AgentDesk-0.2.0-rollback-to-0.1.0.zip"
    if (-not [System.String]::Equals(
            [System.IO.Path]::GetFullPath([string]$bundlePath),
            [System.IO.Path]::GetFullPath($expectedBundle),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Rollback bundler returned an unexpected path: $bundlePath"
    }
    if (-not (Test-Path -LiteralPath $expectedBundle -PathType Leaf)) {
        throw "Rollback bundle was not created."
    }
    $bundleChecksumPath = "$expectedBundle.sha256"
    if (-not (Test-Path -LiteralPath $bundleChecksumPath -PathType Leaf)) {
        throw "Rollback bundle checksum companion was not created."
    }
    $expectedHash = (Get-FileHash -LiteralPath $expectedBundle -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = (Get-Content -LiteralPath $bundleChecksumPath -Raw).Trim()
    if ($checksumLine -cne "$expectedHash  $([System.IO.Path]::GetFileName($expectedBundle))") {
        throw "Rollback bundle checksum companion does not match the archive."
    }

    Expand-Archive -LiteralPath $expectedBundle -DestinationPath $expandedRoot
    $archivedMutationAsset = Join-Path $expandedRoot (
        "assets\" + [System.IO.Path]::GetFileName($mutationSourceAsset))
    $archivedMutationHash = (Get-FileHash -LiteralPath $archivedMutationAsset -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($archivedMutationHash -cne $stableMutationHash) {
        throw "The rollback bundle archived bytes that changed after stable staging."
    }
    $manifestPath = Join-Path $expandedRoot "ROLLBACK-MANIFEST.json"
    $instructionsPath = Join-Path $expandedRoot "ROLLBACK-INSTRUCTIONS.md"
    $instructionsZhCnPath = Join-Path $expandedRoot "ROLLBACK-INSTRUCTIONS.zh-CN.md"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $instructionsPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $instructionsZhCnPath -PathType Leaf)) {
        throw "Rollback archive is missing its manifest or instructions."
    }
    $instructionsZhCn = [System.IO.File]::ReadAllText(
        $instructionsZhCnPath,
        [System.Text.UTF8Encoding]::new($false, $true))
    if (-not $instructionsZhCn.Contains("# AgentDesk 手动回滚") -or
        -not $instructionsZhCn.Contains(
            "https://github.com/rkshadow999/AgentDesk/releases/tag/v0.1.0")) {
        throw "Rollback archive Chinese instructions are incomplete or not valid UTF-8."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or
        $manifest.currentTag -cne "v0.2.0" -or
        $manifest.previousTag -cne "v0.1.0" -or
        @($manifest.architectures).Count -ne 2 -or
        @($manifest.assets).Count -ne 10) {
        throw "Rollback manifest does not describe the verified two-architecture asset set."
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    Add-Content `
        -LiteralPath (Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-portable.zip") `
        -Value "tampered"
    Invoke-ExpectedFailure -MessageFragment "SHA-256 mismatch" -Action {
        & $bundler `
            -CurrentVersion "0.2.0" `
            -CurrentTag "v0.2.0" `
            -PreviousVersion "0.1.0" `
            -PreviousTag "v0.1.0" `
            -PreviousReleaseRoot $previousRoot `
            -OutputRoot $outputRoot `
            -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
            -PreviousUpdateManifestPath $previousUpdateManifest `
            -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
            -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
            -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 `
            -ExpectedPreviousMsixSignerThumbprint $trustedThumbprint `
            -SignToolPath $fakeSignTool `
            -RequireSignedMsix | Out-Null
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    Write-Utf8NoBom `
        -Path (Join-Path $previousRoot "AgentDesk-0.1.0-win-arm64-MSIX-SIGNING-STATUS.txt") `
        -Content "unsigned`n"
    $arm64Prefix = "AgentDesk-0.1.0-win-arm64"
    $arm64ChecksumLines = Get-ChildItem -LiteralPath $previousRoot -File -Filter "$arm64Prefix*" |
        Where-Object Name -NotLike "*-SHA256SUMS.txt" |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $($_.Name)"
        }
    Write-Utf8NoBom `
        -Path (Join-Path $previousRoot "$arm64Prefix-SHA256SUMS.txt") `
        -Content (($arm64ChecksumLines -join "`n") + "`n")
    Invoke-ExpectedFailure -MessageFragment "is not signed" -Action {
        & $bundler `
            -CurrentVersion "0.2.0" `
            -CurrentTag "v0.2.0" `
            -PreviousVersion "0.1.0" `
            -PreviousTag "v0.1.0" `
            -PreviousReleaseRoot $previousRoot `
            -OutputRoot $outputRoot `
            -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
            -PreviousUpdateManifestPath $previousUpdateManifest `
            -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
            -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
            -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 `
            -ExpectedPreviousMsixSignerThumbprint $trustedThumbprint `
            -SignToolPath $fakeSignTool `
            -RequireSignedMsix | Out-Null
    }

    Write-RollbackFixture -Root $previousRoot -UpdateSigningKey $updateSigningKey
    $maliciousChecksum = ("0" * 64) + "  ../outside.txt`n"
    Write-Utf8NoBom `
        -Path (Join-Path $previousRoot "AgentDesk-0.1.0-win-x64-SHA256SUMS.txt") `
        -Content $maliciousChecksum
    Invoke-ExpectedFailure -MessageFragment "unsafe asset name" -Action {
        & $bundler `
            -CurrentVersion "0.2.0" `
            -CurrentTag "v0.2.0" `
            -PreviousVersion "0.1.0" `
            -PreviousTag "v0.1.0" `
            -PreviousReleaseRoot $previousRoot `
            -OutputRoot $outputRoot `
            -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
            -PreviousUpdateManifestPath $previousUpdateManifest `
            -PreviousUpdateManifestSignaturePath $previousUpdateManifestSignature `
            -TrustedUpdatePublicKeyPath $trustedUpdatePublicKey `
            -ExpectedPreviousUpdatePublicKeySha256 $trustedUpdatePublicKeySha256 `
            -ExpectedPreviousMsixSignerThumbprint $trustedThumbprint `
            -SignToolPath $fakeSignTool `
            -RequireSignedMsix | Out-Null
    }
}
finally {
    if ($null -ne $updateSigningKey) {
        $updateSigningKey.Dispose()
    }
    Remove-Item Function:\global:Get-AuthenticodeSignature -ErrorAction SilentlyContinue
    Remove-Variable AgentDeskTestSignerSubject -Scope Global -ErrorAction SilentlyContinue
    Remove-Variable AgentDeskTestSignerThumbprint -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "AgentDesk rollback bundle tests passed."
