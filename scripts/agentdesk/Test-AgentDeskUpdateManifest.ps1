<#
.SYNOPSIS
Runs focused regression tests for deterministic, signed AgentDesk update metadata.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$manifestGenerator = Join-Path $PSScriptRoot "New-AgentDeskUpdateManifest.ps1"
$stableFileModule = Join-Path $PSScriptRoot "AgentDesk.ReleaseFileSafety.psm1"
if (-not (Test-Path -LiteralPath $stableFileModule -PathType Leaf)) {
    throw "The stable release-file module is missing: $stableFileModule"
}
Import-Module $stableFileModule -Force

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessageFragment
    )

    try {
        & $Action
    }
    catch {
        if (-not $_.Exception.Message.Contains(
                $MessageFragment,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Expected failure containing '$MessageFragment', got: $($_.Exception.Message)"
        }
        return
    }

    throw "Expected the action to fail with '$MessageFragment'."
}

function Invoke-Generator {
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$X64Package,
        [Parameter(Mandatory)][string]$Arm64Package,
        [Parameter(Mandatory)][string]$PublicKey,
        [string]$X64Updater,
        [string]$Arm64Updater,
        [string]$PrivateKey,
        [string]$PrivateKeyEnvironmentVariable
    )

    if ([string]::IsNullOrWhiteSpace($X64Updater)) {
        $X64Updater = $X64Package -replace '-portable\.zip$', '-updater.zip'
    }
    if ([string]::IsNullOrWhiteSpace($Arm64Updater)) {
        $Arm64Updater = $Arm64Package -replace '-portable\.zip$', '-updater.zip'
    }

    $parameters = @{
        Version = "1.2.3"
        Repository = "rkshadow999/AgentDesk"
        Tag = "v1.2.3"
        X64PackagePath = $X64Package
        Arm64PackagePath = $Arm64Package
        X64UpdaterPath = $X64Updater
        Arm64UpdaterPath = $Arm64Updater
        PublicKeyPath = $PublicKey
        OutputDirectory = $OutputDirectory
    }
    if ($PrivateKey) {
        $parameters.PrivateKeyPath = $PrivateKey
    }
    if ($PrivateKeyEnvironmentVariable) {
        $parameters.PrivateKeyEnvironmentVariable = $PrivateKeyEnvironmentVariable
    }

    & $manifestGenerator @parameters | Out-Null
}

function Write-MinimalPeFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][uint16]$Machine
    )

    $bytes = [byte[]]::new(512)
    $bytes[0] = 0x4d
    $bytes[1] = 0x5a
    [BitConverter]::GetBytes([int]0x80).CopyTo($bytes, 0x3c)
    $bytes[0x80] = 0x50
    $bytes[0x81] = 0x45
    [BitConverter]::GetBytes($Machine).CopyTo($bytes, 0x84)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Write-UpdaterArchiveFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][uint16]$Machine
    )

    $stagingRoot = Join-Path ([System.IO.Path]::GetDirectoryName($Path)) (
        ".updater-fixture-" + [guid]::NewGuid().ToString("N"))
    try {
        [System.IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
        Write-MinimalPeFixture `
            -Path (Join-Path $stagingRoot "AgentDesk.Updater.exe") `
            -Machine $Machine
        Compress-Archive `
            -Path (Join-Path $stagingRoot "AgentDesk.Updater.exe") `
            -DestinationPath $Path `
            -CompressionLevel Optimal
    }
    finally {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-update-manifest-test-" + [guid]::NewGuid().ToString("N"))
$privateKeyBytes = $null
try {
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $snapshotSource = Join-Path $fixtureRoot "snapshot-source.bin"
    $snapshotStage = Join-Path $fixtureRoot "snapshot-stage"
    $snapshotBytes = [System.Text.Encoding]::UTF8.GetBytes("stable-release-bytes")
    [System.IO.File]::WriteAllBytes($snapshotSource, $snapshotBytes)
    $snapshot = New-AgentDeskStableFileSnapshot `
        -SourcePath $snapshotSource `
        -StageRoot $snapshotStage `
        -FileName "snapshot.bin" `
        -MaximumBytes 1024 `
        -Description "test release asset"
    try {
        [System.IO.File]::WriteAllText($snapshotSource, "mutated source")
        if ($snapshot.Sha256 -cne
            [Convert]::ToHexStringLower([System.Security.Cryptography.SHA256]::HashData($snapshotBytes))) {
            throw "The stable snapshot hash changed after the source file was replaced."
        }
        $snapshot.Stream.Position = 0
        $capturedBytes = [byte[]]::new($snapshot.Length)
        $capturedCount = $snapshot.Stream.Read($capturedBytes, 0, $capturedBytes.Length)
        if ($capturedCount -ne $snapshotBytes.Length -or
            -not [System.Linq.Enumerable]::SequenceEqual($capturedBytes, $snapshotBytes)) {
            throw "The stable snapshot bytes changed after the source file was replaced."
        }
        $stageWriteSucceeded = $false
        try {
            [System.IO.File]::WriteAllText($snapshot.Path, "replace locked staging")
            $stageWriteSucceeded = $true
        }
        catch [System.IO.IOException] {
        }
        if ($stageWriteSucceeded) {
            throw "The stable staging file allowed a concurrent writer while its snapshot was active."
        }
    }
    finally {
        Close-AgentDeskStableFileSnapshot -Snapshot $snapshot
    }

    $x64Package = Join-Path $fixtureRoot "AgentDesk-1.2.3-win-x64-portable.zip"
    $arm64Package = Join-Path $fixtureRoot "AgentDesk-1.2.3-win-arm64-portable.zip"
    $x64Updater = Join-Path $fixtureRoot "AgentDesk-1.2.3-win-x64-updater.zip"
    $arm64Updater = Join-Path $fixtureRoot "AgentDesk-1.2.3-win-arm64-updater.zip"
    [System.IO.File]::WriteAllBytes($x64Package, [byte[]](0..63))
    [System.IO.File]::WriteAllBytes($arm64Package, [byte[]](255..128))
    Write-UpdaterArchiveFixture -Path $x64Updater -Machine 0x8664
    Write-UpdaterArchiveFixture -Path $arm64Updater -Machine 0xaa64

    $key = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $privateKeyBytes = $key.ExportPkcs8PrivateKey()
        $privateKeyPath = Join-Path $fixtureRoot "update-private-key.pk8"
        $publicKeyPath = Join-Path $fixtureRoot "update-public-key.spki"
        [System.IO.File]::WriteAllBytes($privateKeyPath, $privateKeyBytes)
        [System.IO.File]::WriteAllBytes($publicKeyPath, $key.ExportSubjectPublicKeyInfo())
    }
    finally {
        $key.Dispose()
    }

    $outputOne = Join-Path $fixtureRoot "output-one"
    $outputTwo = Join-Path $fixtureRoot "output-two"
    Invoke-Generator `
        -OutputDirectory $outputOne `
        -X64Package $x64Package `
        -Arm64Package $arm64Package `
        -PublicKey $publicKeyPath `
        -PrivateKey $privateKeyPath
    Invoke-Generator `
        -OutputDirectory $outputTwo `
        -X64Package $x64Package `
        -Arm64Package $arm64Package `
        -PublicKey $publicKeyPath `
        -PrivateKey $privateKeyPath

    $manifestName = "AgentDesk-update-manifest.json"
    $signatureName = "AgentDesk-update-manifest.json.sig"
    $updaterManifestName = "AgentDesk-updater-manifest.json"
    $updaterSignatureName = "AgentDesk-updater-manifest.json.sig"
    $publishedKeyName = "AgentDesk-update-public-key.spki"
    $checksumsName = "AgentDesk-update-metadata-SHA256SUMS.txt"
    $manifestOne = [System.IO.File]::ReadAllBytes((Join-Path $outputOne $manifestName))
    $manifestTwo = [System.IO.File]::ReadAllBytes((Join-Path $outputTwo $manifestName))
    if (-not [System.Linq.Enumerable]::SequenceEqual($manifestOne, $manifestTwo)) {
        throw "Update manifest bytes must be deterministic for identical release inputs."
    }
    $updaterManifestOne = [System.IO.File]::ReadAllBytes((Join-Path $outputOne $updaterManifestName))
    $updaterManifestTwo = [System.IO.File]::ReadAllBytes((Join-Path $outputTwo $updaterManifestName))
    if (-not [System.Linq.Enumerable]::SequenceEqual($updaterManifestOne, $updaterManifestTwo)) {
        throw "Updater manifest bytes must be deterministic for identical release inputs."
    }

    $manifest = [System.Text.Encoding]::UTF8.GetString($manifestOne) | ConvertFrom-Json
    if (@($manifest.psobject.Properties.Name) -join "," -ne "schemaVersion,product,version,assets" -or
        $manifest.schemaVersion -ne 1 -or
        $manifest.product -ne "AgentDesk" -or
        $manifest.version -ne "1.2.3") {
        throw "Update manifest root metadata or property order is invalid."
    }
    $assets = @($manifest.assets)
    if ($assets.Count -ne 2 -or
        $assets[0].architecture -ne "x64" -or
        $assets[1].architecture -ne "arm64") {
        throw "Update manifest assets must contain x64 then arm64 deterministically."
    }
    foreach ($assetFixture in @(
        @{ Asset = $assets[0]; Path = $x64Package; Architecture = "x64" },
        @{ Asset = $assets[1]; Path = $arm64Package; Architecture = "arm64" }
    )) {
        $expectedHash = (Get-FileHash -LiteralPath $assetFixture.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedSize = (Get-Item -LiteralPath $assetFixture.Path).Length
        $expectedUrl = "https://github.com/rkshadow999/AgentDesk/releases/download/v1.2.3/" +
            [System.IO.Path]::GetFileName($assetFixture.Path)
        if ($assetFixture.Asset.sha256 -cne $expectedHash -or
            $assetFixture.Asset.size -ne $expectedSize -or
            $assetFixture.Asset.url -cne $expectedUrl -or
            $assetFixture.Asset.entryPoint -cne "AgentDesk.App.exe") {
            throw "Update manifest metadata is invalid for $($assetFixture.Architecture)."
        }
    }

    $updaterManifest = [System.Text.Encoding]::UTF8.GetString($updaterManifestOne) | ConvertFrom-Json
    if (@($updaterManifest.psobject.Properties.Name) -join "," -ne "schemaVersion,product,version,assets" -or
        $updaterManifest.schemaVersion -ne 1 -or
        $updaterManifest.product -ne "AgentDesk.Updater" -or
        $updaterManifest.version -ne "1.2.3") {
        throw "Updater manifest root metadata or property order is invalid."
    }
    $updaterAssets = @($updaterManifest.assets)
    if ($updaterAssets.Count -ne 2 -or
        $updaterAssets[0].architecture -ne "x64" -or
        $updaterAssets[1].architecture -ne "arm64") {
        throw "Updater manifest assets must contain x64 then arm64 deterministically."
    }
    foreach ($assetFixture in @(
        @{ Asset = $updaterAssets[0]; Path = $x64Updater; Architecture = "x64" },
        @{ Asset = $updaterAssets[1]; Path = $arm64Updater; Architecture = "arm64" }
    )) {
        $expectedHash = (Get-FileHash -LiteralPath $assetFixture.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedSize = (Get-Item -LiteralPath $assetFixture.Path).Length
        $expectedUrl = "https://github.com/rkshadow999/AgentDesk/releases/download/v1.2.3/" +
            [System.IO.Path]::GetFileName($assetFixture.Path)
        if ($assetFixture.Asset.sha256 -cne $expectedHash -or
            $assetFixture.Asset.size -ne $expectedSize -or
            $assetFixture.Asset.url -cne $expectedUrl -or
            $assetFixture.Asset.entryPoint -cne "AgentDesk.Updater.exe" -or
            @($assetFixture.Asset.psobject.Properties.Name) -join "," -ne "architecture,url,sha256,size,entryPoint") {
            throw "Updater manifest metadata is invalid for $($assetFixture.Architecture)."
        }
    }

    $verifier = [System.Security.Cryptography.ECDsa]::Create()
    try {
        $publicKeyBytes = [System.IO.File]::ReadAllBytes($publicKeyPath)
        $bytesRead = 0
        $verifier.ImportSubjectPublicKeyInfo($publicKeyBytes, [ref]$bytesRead)
        if ($bytesRead -ne $publicKeyBytes.Length) {
            throw "The public verification key contains trailing data."
        }
        foreach ($signedMetadata in @(
            @{ Bytes = $manifestOne; Signature = $signatureName; Description = "update" },
            @{ Bytes = $updaterManifestOne; Signature = $updaterSignatureName; Description = "updater" }
        )) {
            $signature = [System.IO.File]::ReadAllBytes((Join-Path $outputOne $signedMetadata.Signature))
            if (-not $verifier.VerifyData(
                    $signedMetadata.Bytes,
                    $signature,
                    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                    [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
                throw "Detached $($signedMetadata.Description) manifest signature is not a valid ECDSA P-256 DER signature."
            }
        }
    }
    finally {
        $verifier.Dispose()
    }

    if (-not [System.Linq.Enumerable]::SequenceEqual(
            [System.IO.File]::ReadAllBytes($publicKeyPath),
            [System.IO.File]::ReadAllBytes((Join-Path $outputOne $publishedKeyName)))) {
        throw "The published update public key must exactly match the verified input key."
    }
    $checksumLines = Get-Content -LiteralPath (Join-Path $outputOne $checksumsName)
    foreach ($requiredMetadataFile in @(
        $manifestName,
        $signatureName,
        $updaterManifestName,
        $updaterSignatureName,
        $publishedKeyName
    )) {
        if (-not ($checksumLines | Where-Object { $_.EndsWith("  $requiredMetadataFile") })) {
            throw "Update metadata checksums are missing $requiredMetadataFile."
        }
    }
    if (Get-ChildItem -LiteralPath $outputOne -File |
        Where-Object { $_.Name -match "private|pk8|pkcs8" }) {
        throw "Private update signing material must never be copied to release output."
    }

    $environmentVariable = "AGENTDESK_UPDATE_TEST_KEY_" + [guid]::NewGuid().ToString("N")
    try {
        [Environment]::SetEnvironmentVariable(
            $environmentVariable,
            [Convert]::ToBase64String($privateKeyBytes),
            [EnvironmentVariableTarget]::Process)
        Invoke-Generator `
            -OutputDirectory (Join-Path $fixtureRoot "output-env") `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -PublicKey $publicKeyPath `
            -PrivateKeyEnvironmentVariable $environmentVariable
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            $environmentVariable,
            $null,
            [EnvironmentVariableTarget]::Process)
    }

    Invoke-ExpectedFailure -MessageFragment "private key" -Action {
        Invoke-Generator `
            -OutputDirectory (Join-Path $fixtureRoot "output-no-private") `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -PublicKey $publicKeyPath
    }
    Invoke-ExpectedFailure -MessageFragment "public key" -Action {
        Invoke-Generator `
            -OutputDirectory (Join-Path $fixtureRoot "output-no-public") `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -PublicKey (Join-Path $fixtureRoot "missing.spki") `
            -PrivateKey $privateKeyPath
    }
    Invoke-ExpectedFailure -MessageFragment "x64 updater" -Action {
        Invoke-Generator `
            -OutputDirectory (Join-Path $fixtureRoot "output-missing-updater") `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -X64Updater (Join-Path $fixtureRoot "missing-updater.zip") `
            -Arm64Updater $arm64Updater `
            -PublicKey $publicKeyPath `
            -PrivateKey $privateKeyPath
    }
    $swappedRoot = Join-Path $fixtureRoot "swapped-updaters"
    [System.IO.Directory]::CreateDirectory($swappedRoot) | Out-Null
    $swappedX64Updater = Join-Path $swappedRoot "AgentDesk-1.2.3-win-x64-updater.zip"
    $swappedArm64Updater = Join-Path $swappedRoot "AgentDesk-1.2.3-win-arm64-updater.zip"
    Write-UpdaterArchiveFixture -Path $swappedX64Updater -Machine 0xaa64
    Write-UpdaterArchiveFixture -Path $swappedArm64Updater -Machine 0x8664
    Invoke-ExpectedFailure -MessageFragment "architecture" -Action {
        Invoke-Generator `
            -OutputDirectory (Join-Path $fixtureRoot "output-swapped-updaters") `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -X64Updater $swappedX64Updater `
            -Arm64Updater $swappedArm64Updater `
            -PublicKey $publicKeyPath `
            -PrivateKey $privateKeyPath
    }
    Invoke-ExpectedFailure -MessageFragment "temporary" -Action {
        Invoke-Generator `
            -OutputDirectory (Join-Path $fixtureRoot "output-repository-private") `
            -X64Package $x64Package `
            -Arm64Package $arm64Package `
            -PublicKey $publicKeyPath `
            -PrivateKey $manifestGenerator
    }
    if ($IsWindows) {
        $junctionTarget = Join-Path $env:LOCALAPPDATA (
            "agentdesk-update-key-junction-test-" + [guid]::NewGuid().ToString("N"))
        $junctionPath = Join-Path $fixtureRoot "private-key-junction"
        try {
            [System.IO.Directory]::CreateDirectory($junctionTarget) | Out-Null
            $junctionPrivateKey = Join-Path $junctionTarget "update-private-key.pk8"
            [System.IO.File]::WriteAllBytes($junctionPrivateKey, $privateKeyBytes)
            New-Item -ItemType Junction -Path $junctionPath -Target $junctionTarget | Out-Null
            Invoke-ExpectedFailure -MessageFragment "reparse point" -Action {
                Invoke-Generator `
                    -OutputDirectory (Join-Path $fixtureRoot "output-junction-private") `
                    -X64Package $x64Package `
                    -Arm64Package $arm64Package `
                    -PublicKey $publicKeyPath `
                    -PrivateKey (Join-Path $junctionPath "update-private-key.pk8")
            }
        }
        finally {
            Remove-Item -LiteralPath $junctionPath -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $junctionTarget -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $wrongCurve = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP384)
    try {
        $wrongPrivate = Join-Path $fixtureRoot "wrong-private-key.pk8"
        $wrongPublic = Join-Path $fixtureRoot "wrong-public-key.spki"
        [System.IO.File]::WriteAllBytes($wrongPrivate, $wrongCurve.ExportPkcs8PrivateKey())
        [System.IO.File]::WriteAllBytes($wrongPublic, $wrongCurve.ExportSubjectPublicKeyInfo())
        Invoke-ExpectedFailure -MessageFragment "P-256" -Action {
            Invoke-Generator `
                -OutputDirectory (Join-Path $fixtureRoot "output-wrong-curve") `
                -X64Package $x64Package `
                -Arm64Package $arm64Package `
                -PublicKey $wrongPublic `
                -PrivateKey $wrongPrivate
        }
    }
    finally {
        $wrongCurve.Dispose()
    }
}
finally {
    if ($null -ne $privateKeyBytes) {
        [System.Array]::Clear($privateKeyBytes, 0, $privateKeyBytes.Length)
    }
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "AgentDesk update manifest tests passed."
