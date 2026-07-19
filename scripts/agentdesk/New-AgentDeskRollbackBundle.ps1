<#
.SYNOPSIS
Builds a verified rollback bundle from a previous AgentDesk GitHub Release.

.DESCRIPTION
Validates the previous release's per-architecture SHA256SUMS files, requires the
expected Portable, MSIX, SBOM, and signing-status assets, and writes a zip with
bilingual manual rollback instructions and a machine-readable manifest.

.PARAMETER RequireSignedMsix
Rejects a previous release unless both architecture status files contain
exactly "signed" and both staged MSIX packages pass cryptographic verification.
Tag release automation must always set this switch.

.PARAMETER ExpectedPreviousMsixSignerThumbprint
Repository-pinned SHA-1 thumbprint for the previous release MSIX signer.
Tag rollback generation requires this independent historical pin.

.PARAMETER PreviousUpdateManifestPath
Path to the previous release's signed AgentDesk application update manifest.

.PARAMETER PreviousUpdateManifestSignaturePath
Path to the detached ECDSA signature for PreviousUpdateManifestPath.

.PARAMETER TrustedUpdatePublicKeyPath
Path to the ECDSA P-256 SPKI public key used to verify the previous release
update manifest. Its stable bytes must match ExpectedPreviousUpdatePublicKeySha256.

.PARAMETER ExpectedPreviousUpdatePublicKeySha256
Independent repository pin for the SHA-256 of TrustedUpdatePublicKeyPath. This
allows update-signing key rotation without trusting a mutable Release asset.

.PARAMETER SignToolPath
Optional explicit path to signtool.exe used by MSIX signature verification.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CurrentVersion,
    [Parameter(Mandatory)][string]$CurrentTag,
    [Parameter(Mandatory)][string]$PreviousVersion,
    [Parameter(Mandatory)][string]$PreviousTag,
    [Parameter(Mandatory)][string]$PreviousReleaseRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$SourceRepository,
    [ValidateSet("x64", "arm64")][string[]]$Architectures = @("x64", "arm64"),
    [string]$PreviousUpdateManifestPath,
    [string]$PreviousUpdateManifestSignaturePath,
    [string]$TrustedUpdatePublicKeyPath,
    [string]$ExpectedPreviousUpdatePublicKeySha256,
    [string]$ExpectedPreviousMsixSignerThumbprint,
    [string]$SignToolPath,
    [switch]$RequireSignedMsix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$releaseFileSafetyModule = Join-Path $PSScriptRoot "AgentDesk.ReleaseFileSafety.psm1"
Import-Module -Name $releaseFileSafetyModule -Force -ErrorAction Stop
$signatureVerifier = Join-Path $PSScriptRoot "Verify-AgentDeskMsixSignature.ps1"

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Assert-SafeLabel {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value.Length -gt 128 -or
        $Value -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
        throw "$Name contains unsupported characters."
    }
}

function ConvertTo-MsixIdentityVersion {
    param(
        [Parameter(Mandatory)][string]$InputVersion,
        [Parameter(Mandatory)][string]$VersionName
    )

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $InputVersion,
        '^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(ci|alpha|beta|preview|rc)\.(0|[1-9]\d*))?$')
    if (-not $match.Success) {
        throw "$VersionName must be stable SemVer or a supported numbered prerelease."
    }

    $components = foreach ($groupIndex in 1..3) {
        [uint64]$component = 0
        if (-not [uint64]::TryParse($match.Groups[$groupIndex].Value, [ref]$component) -or
            $component -gt 65535) {
            throw "$VersionName components must be between 0 and 65535."
        }
        [int]$component
    }

    $revision = 65535
    if ($match.Groups[4].Success) {
        [uint64]$sequence = 0
        if (-not [uint64]::TryParse($match.Groups[5].Value, [ref]$sequence) -or
            $sequence -gt 9999) {
            throw "$VersionName prerelease sequence must be between 0 and 9999."
        }
        $channelOffsets = @{
            ci = 0
            alpha = 10000
            beta = 20000
            preview = 30000
            rc = 40000
        }
        $revision = $channelOffsets[$match.Groups[4].Value] + [int]$sequence
    }

    return "$($components[0]).$($components[1]).$($components[2]).$revision"
}

function Read-StableSnapshotBytes {
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][long]$MaximumBytes
    )

    if ($Snapshot.Length -le 0 -or $Snapshot.Length -gt $MaximumBytes) {
        throw "The stable snapshot bytes are missing or oversized."
    }
    $stream = [System.IO.FileStream]$Snapshot.Stream
    $bytes = [byte[]]::new($Snapshot.Length)
    $offset = 0
    try {
        $stream.Position = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) {
                throw "The stable snapshot bytes ended unexpectedly."
            }
            $offset += $read
        }
        return ,$bytes
    }
    finally {
        $stream.Position = 0
    }
}

function Assert-P256PublicKey {
    param([Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$Key)

    $parameters = $Key.ExportParameters($false)
    if ($Key.KeySize -ne 256 -or
        $parameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7") {
        throw "The trusted update public key must be ECDSA P-256."
    }
}

function Assert-ManifestObjectProperties {
    param(
        [Parameter(Mandatory)][System.Text.Json.JsonElement]$Element,
        [Parameter(Mandatory)][string[]]$Expected
    )

    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "The previous update manifest structure is invalid."
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($property.Name -notin $Expected -or -not $seen.Add($property.Name)) {
            throw "The previous update manifest contains unknown or duplicate fields."
        }
    }
    if ($seen.Count -ne $Expected.Count) {
        throw "The previous update manifest is missing required fields."
    }
}

function Read-PreviousUpdateManifestAssets {
    param(
        [Parameter(Mandatory)][byte[]]$ManifestBytes,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedTag,
        [Parameter(Mandatory)][string]$ExpectedRepository
    )

    $strictUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    [void]$strictUtf8.GetString($ManifestBytes)
    $options = [System.Text.Json.JsonDocumentOptions]::new()
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [System.Text.Json.JsonCommentHandling]::Disallow
    $options.MaxDepth = 8
    $document = [System.Text.Json.JsonDocument]::Parse(
        [System.ReadOnlyMemory[byte]]::new($ManifestBytes),
        $options)
    try {
        $root = $document.RootElement
        Assert-ManifestObjectProperties `
            -Element $root `
            -Expected @("schemaVersion", "product", "version", "assets")
        if ($root.GetProperty("schemaVersion").GetInt32() -ne 1 -or
            $root.GetProperty("product").GetString() -cne "AgentDesk" -or
            $root.GetProperty("version").GetString() -cne $ExpectedVersion) {
            throw "The previous update manifest identity is invalid."
        }

        $assetsElement = $root.GetProperty("assets")
        if ($assetsElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array -or
            $assetsElement.GetArrayLength() -ne 2) {
            throw "The previous update manifest must contain exactly x64 and arm64 assets."
        }
        $assets = @{}
        foreach ($assetElement in $assetsElement.EnumerateArray()) {
            Assert-ManifestObjectProperties `
                -Element $assetElement `
                -Expected @("architecture", "url", "sha256", "size", "entryPoint")
            $architecture = $assetElement.GetProperty("architecture").GetString()
            if ($architecture -notin @("x64", "arm64") -or $assets.ContainsKey($architecture)) {
                throw "The previous update manifest architecture set is invalid."
            }
            $name = "AgentDesk-$ExpectedVersion-win-$architecture-portable.zip"
            $expectedUrl = $ExpectedRepository.TrimEnd('/') +
                "/releases/download/$ExpectedTag/$name"
            $url = $assetElement.GetProperty("url").GetString()
            $sha256 = $assetElement.GetProperty("sha256").GetString()
            $size = $assetElement.GetProperty("size").GetInt64()
            $entryPoint = $assetElement.GetProperty("entryPoint").GetString()
            if ($url -cne $expectedUrl -or
                $sha256 -notmatch '^[0-9a-f]{64}$' -or
                $size -le 0 -or
                $size -gt 512L * 1024 * 1024 -or
                $entryPoint -cne "AgentDesk.App.exe") {
                throw "The previous update manifest asset metadata is invalid."
            }
            $assets[$architecture] = [pscustomobject]@{
                Name = $name
                Sha256 = $sha256
                Size = $size
            }
        }
        if ($assets.Count -ne 2 -or
            -not $assets.ContainsKey("x64") -or
            -not $assets.ContainsKey("arm64")) {
            throw "The previous update manifest must contain exactly x64 and arm64 assets."
        }
        return $assets
    }
    finally {
        $document.Dispose()
    }
}

function Open-VerifiedRollbackArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)]
        [System.Collections.Generic.Dictionary[string, string]]$ExpectedAssetHashes
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = $null
    $archive = $null
    $hasher = $null
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $archive = [System.IO.Compression.ZipArchive]::new(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $true)
        $seenAssets = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.StartsWith("assets/", [System.StringComparison]::Ordinal) -or
                $entry.FullName.EndsWith("/", [System.StringComparison]::Ordinal)) {
                continue
            }
            if (-not $ExpectedAssetHashes.ContainsKey($entry.FullName)) {
                throw "Rollback archive contains an unexpected asset entry: $($entry.FullName)"
            }
            if (-not $seenAssets.Add($entry.FullName)) {
                throw "Rollback archive contains a duplicate asset entry: $($entry.FullName)"
            }

            $entryStream = $entry.Open()
            $entryHasher = [System.Security.Cryptography.SHA256]::Create()
            try {
                $entryHash = [Convert]::ToHexStringLower($entryHasher.ComputeHash($entryStream))
            }
            finally {
                $entryHasher.Dispose()
                $entryStream.Dispose()
            }
            if ($entryHash -cne $ExpectedAssetHashes[$entry.FullName]) {
                throw "Rollback archive asset bytes do not match stable staging: $($entry.FullName)"
            }
        }
        foreach ($expectedPath in $ExpectedAssetHashes.Keys) {
            if (-not $seenAssets.Contains($expectedPath)) {
                throw "Rollback archive is missing a staged asset entry: $expectedPath"
            }
        }

        $archive.Dispose()
        $archive = $null
        $stream.Position = 0
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        $archiveHash = [Convert]::ToHexStringLower($hasher.ComputeHash($stream))
        $stream.Position = 0
        $result = [pscustomobject]@{
            Stream = $stream
            Sha256 = $archiveHash
        }
        $stream = $null
        return $result
    }
    catch [System.IO.InvalidDataException] {
        throw "The rollback archive is not a valid zip file."
    }
    finally {
        if ($null -ne $hasher) {
            $hasher.Dispose()
        }
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

foreach ($entry in @(
    @{ Name = "CurrentVersion"; Value = $CurrentVersion },
    @{ Name = "CurrentTag"; Value = $CurrentTag },
    @{ Name = "PreviousVersion"; Value = $PreviousVersion },
    @{ Name = "PreviousTag"; Value = $PreviousTag }
)) {
    Assert-SafeLabel -Name $entry.Name -Value $entry.Value
}
if ([string]::IsNullOrWhiteSpace($SourceRepository) -or
    -not [Uri]::IsWellFormedUriString($SourceRepository, [UriKind]::Absolute)) {
    throw "SourceRepository must be an absolute URI."
}
if ($CurrentTag -cne "v$CurrentVersion" -or $PreviousTag -cne "v$PreviousVersion") {
    throw "CurrentTag and PreviousTag must match their corresponding versions."
}

$currentMsixVersion = ConvertTo-MsixIdentityVersion `
    -InputVersion $CurrentVersion `
    -VersionName "CurrentVersion"
$previousMsixVersion = ConvertTo-MsixIdentityVersion `
    -InputVersion $PreviousVersion `
    -VersionName "PreviousVersion"
if ([Version]$previousMsixVersion -ge [Version]$currentMsixVersion) {
    throw "PreviousVersion must be lower than CurrentVersion."
}

$PreviousReleaseRoot = Get-FullPath $PreviousReleaseRoot
$OutputRoot = Get-FullPath $OutputRoot
if (-not (Test-Path -LiteralPath $PreviousReleaseRoot -PathType Container)) {
    throw "Previous release directory was not found: $PreviousReleaseRoot"
}
if ($Architectures.Count -ne 2 -or
    @($Architectures | Sort-Object -Unique).Count -ne 2 -or
    "x64" -notin $Architectures -or
    "arm64" -notin $Architectures) {
    throw "Rollback bundles must contain exactly the x64 and arm64 release assets."
}
if ([string]::IsNullOrWhiteSpace($PreviousUpdateManifestPath) -or
    [string]::IsNullOrWhiteSpace($PreviousUpdateManifestSignaturePath) -or
    [string]::IsNullOrWhiteSpace($TrustedUpdatePublicKeyPath)) {
    throw "A signed previous update manifest, detached signature, and trusted public key are required for Portable rollback verification."
}
if ([string]::IsNullOrWhiteSpace($ExpectedPreviousUpdatePublicKeySha256)) {
    throw "The previous update public key SHA-256 pin is required for Portable rollback verification."
}
$normalizedPreviousUpdatePublicKeySha256 = $ExpectedPreviousUpdatePublicKeySha256.Trim().ToLowerInvariant()
if ($normalizedPreviousUpdatePublicKeySha256 -notmatch '^[0-9a-f]{64}$') {
    throw "The previous update public key SHA-256 pin must contain exactly 64 hexadecimal characters."
}
$PreviousUpdateManifestPath = Get-FullPath $PreviousUpdateManifestPath
$PreviousUpdateManifestSignaturePath = Get-FullPath $PreviousUpdateManifestSignaturePath
$TrustedUpdatePublicKeyPath = Get-FullPath $TrustedUpdatePublicKeyPath
foreach ($requiredTrustFile in @(
    $PreviousUpdateManifestPath,
    $PreviousUpdateManifestSignaturePath,
    $TrustedUpdatePublicKeyPath
)) {
    if (-not (Test-Path -LiteralPath $requiredTrustFile -PathType Leaf)) {
        throw "The signed previous update manifest trust evidence is missing: $requiredTrustFile"
    }
}
if ($RequireSignedMsix -and -not (Test-Path -LiteralPath $signatureVerifier -PathType Leaf)) {
    throw "The repository MSIX signature verifier is missing: $signatureVerifier"
}
$normalizedPreviousSignerThumbprint = $null
if ($RequireSignedMsix) {
    if ([string]::IsNullOrWhiteSpace($ExpectedPreviousMsixSignerThumbprint)) {
        throw "The previous release MSIX signer thumbprint is required for signed rollback verification."
    }
    $normalizedPreviousSignerThumbprint = $ExpectedPreviousMsixSignerThumbprint.Trim().ToUpperInvariant()
    if ($normalizedPreviousSignerThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw "The previous release MSIX signer thumbprint must contain exactly 40 hexadecimal characters."
    }
}

[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
if (([System.IO.File]::GetAttributes($OutputRoot) -band
        [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Rollback output directory must not be a reparse point."
}
$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-rollback-stage-" + [guid]::NewGuid().ToString("N"))
$validationRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-rollback-validation-" + [guid]::NewGuid().ToString("N"))
$assetsRoot = Join-Path $stageRoot "assets"
$safeCurrentVersion = $CurrentVersion -replace '[^0-9A-Za-z._-]', '-'
$safePreviousVersion = $PreviousVersion -replace '[^0-9A-Za-z._-]', '-'
$bundleName = "AgentDesk-$safeCurrentVersion-rollback-to-$safePreviousVersion.zip"
$bundlePath = Join-Path $OutputRoot $bundleName
$bundleChecksumPath = "$bundlePath.sha256"
$stableSnapshots = [System.Collections.Generic.List[object]]::new()
$expectedArchivedAssetHashes = [System.Collections.Generic.Dictionary[string, string]]::new(
    [System.StringComparer]::Ordinal)
$previousUpdateManifestBytes = $null

try {
    [System.IO.Directory]::CreateDirectory($assetsRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($validationRoot) | Out-Null
    $manifestAssets = [System.Collections.Generic.List[object]]::new()
    $checksumDocuments = [System.Collections.Generic.List[object]]::new()

    $previousUpdateManifestSnapshot = New-AgentDeskStableFileSnapshot `
        -SourcePath $PreviousUpdateManifestPath `
        -StageRoot $validationRoot `
        -FileName "AgentDesk-update-manifest.json" `
        -MaximumBytes (1024L * 1024) `
        -Description "previous signed update manifest"
    $stableSnapshots.Add($previousUpdateManifestSnapshot)
    $previousUpdateSignatureSnapshot = New-AgentDeskStableFileSnapshot `
        -SourcePath $PreviousUpdateManifestSignaturePath `
        -StageRoot $validationRoot `
        -FileName "AgentDesk-update-manifest.json.sig" `
        -MaximumBytes 1024 `
        -Description "previous update manifest signature"
    $stableSnapshots.Add($previousUpdateSignatureSnapshot)
    $trustedUpdatePublicKeySnapshot = New-AgentDeskStableFileSnapshot `
        -SourcePath $TrustedUpdatePublicKeyPath `
        -StageRoot $validationRoot `
        -FileName "AgentDesk-update-public-key.spki" `
        -MaximumBytes 1024 `
        -Description "trusted update public key"
    $stableSnapshots.Add($trustedUpdatePublicKeySnapshot)
    if ($trustedUpdatePublicKeySnapshot.Sha256 -cne $normalizedPreviousUpdatePublicKeySha256) {
        throw "The previous update public key does not match the repository-pinned SHA-256."
    }

    $previousUpdateManifestBytes = Read-StableSnapshotBytes `
        -Snapshot $previousUpdateManifestSnapshot `
        -MaximumBytes (1024L * 1024)
    $previousUpdateSignatureBytes = Read-StableSnapshotBytes `
        -Snapshot $previousUpdateSignatureSnapshot `
        -MaximumBytes 1024
    $trustedUpdatePublicKeyBytes = Read-StableSnapshotBytes `
        -Snapshot $trustedUpdatePublicKeySnapshot `
        -MaximumBytes 1024
    $updateVerifier = $null
    try {
        $updateVerifier = [System.Security.Cryptography.ECDsa]::Create()
        $publicKeyBytesRead = 0
        $updateVerifier.ImportSubjectPublicKeyInfo(
            $trustedUpdatePublicKeyBytes,
            [ref]$publicKeyBytesRead)
        if ($publicKeyBytesRead -ne $trustedUpdatePublicKeyBytes.Length) {
            throw "The trusted update public key contains trailing data."
        }
        Assert-P256PublicKey -Key $updateVerifier
        if (-not $updateVerifier.VerifyData(
                $previousUpdateManifestBytes,
                $previousUpdateSignatureBytes,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
            throw "The previous update manifest signature is invalid."
        }
    }
    catch [System.Security.Cryptography.CryptographicException] {
        throw "The previous update manifest signature is invalid."
    }
    finally {
        if ($null -ne $updateVerifier) {
            $updateVerifier.Dispose()
        }
        [System.Array]::Clear(
            $previousUpdateSignatureBytes,
            0,
            $previousUpdateSignatureBytes.Length)
        [System.Array]::Clear(
            $trustedUpdatePublicKeyBytes,
            0,
            $trustedUpdatePublicKeyBytes.Length)
    }
    $previousUpdateAssets = Read-PreviousUpdateManifestAssets `
        -ManifestBytes $previousUpdateManifestBytes `
        -ExpectedVersion $PreviousVersion `
        -ExpectedTag $PreviousTag `
        -ExpectedRepository $SourceRepository

    foreach ($architecture in @($Architectures | Sort-Object)) {
        $prefix = "AgentDesk-$PreviousVersion-win-$architecture"
        $archivedNames = @(
            "$prefix-portable.zip",
            "$prefix.msix",
            "$prefix.spdx.json",
            "$prefix.cyclonedx.json",
            "$prefix-MSIX-SIGNING-STATUS.txt"
        )
        $checksumAssetNames = @(
            "$prefix-portable.zip",
            "$prefix-updater.zip",
            "$prefix-UPDATE-STATUS.txt",
            "$prefix.msix",
            "$prefix.spdx.json",
            "$prefix.cyclonedx.json",
            "$prefix-MSIX-SIGNING-STATUS.txt"
        )
        $checksumName = "$prefix-SHA256SUMS.txt"
        $checksumPath = Join-Path $PreviousReleaseRoot $checksumName
        $checksumSnapshot = New-AgentDeskStableFileSnapshot `
            -SourcePath $checksumPath `
            -StageRoot $assetsRoot `
            -FileName $checksumName `
            -MaximumBytes (4L * 1024 * 1024) `
            -Description "previous release checksum file '$checksumName'"
        $stableSnapshots.Add($checksumSnapshot)

        $allowedChecksumAssetNames = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
        foreach ($checksumAssetName in $checksumAssetNames) {
            [void]$allowedChecksumAssetNames.Add($checksumAssetName)
        }
        $declaredHashes = [System.Collections.Generic.Dictionary[string, string]]::new(
            [System.StringComparer]::Ordinal)
        $checksumText = Read-AgentDeskStableSnapshotText `
            -Snapshot $checksumSnapshot `
            -MaximumCharacters (1024 * 1024)
        foreach ($line in $checksumText -split '\r?\n') {
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }
            $match = [System.Text.RegularExpressions.Regex]::Match(
                $line,
                '^(?<hash>[0-9A-Fa-f]{64})  (?<name>.+)$')
            if (-not $match.Success) {
                throw "Previous release checksum file has an invalid line: $checksumName"
            }
            $assetName = $match.Groups["name"].Value
            if ([System.IO.Path]::GetFileName($assetName) -cne $assetName -or
                $assetName -in @(".", "..") -or
                $assetName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
                throw "Release checksum contains an unsafe asset name: $assetName"
            }
            if (-not $allowedChecksumAssetNames.Contains($assetName)) {
                throw "Previous release checksum lists an unexpected asset: $assetName"
            }
            if ($declaredHashes.ContainsKey($assetName)) {
                throw "Previous release checksum lists an asset more than once: $assetName"
            }
            $declaredHashes[$assetName] = $match.Groups["hash"].Value.ToLowerInvariant()
        }

        $architectureSnapshots = @{}
        foreach ($expectedName in $checksumAssetNames) {
            if (-not $declaredHashes.ContainsKey($expectedName)) {
                throw "Previous release checksum does not cover required asset: $expectedName"
            }
            $snapshotRoot = if ($expectedName -in $archivedNames) {
                $assetsRoot
            }
            else {
                $validationRoot
            }
            $assetSnapshot = New-AgentDeskStableFileSnapshot `
                -SourcePath (Join-Path $PreviousReleaseRoot $expectedName) `
                -StageRoot $snapshotRoot `
                -FileName $expectedName `
                -MaximumBytes (512L * 1024 * 1024) `
                -Description "previous release asset '$expectedName'"
            $stableSnapshots.Add($assetSnapshot)
            if ($assetSnapshot.Sha256 -cne $declaredHashes[$expectedName]) {
                throw "SHA-256 mismatch for previous release asset '$expectedName'."
            }
            $architectureSnapshots[$expectedName] = $assetSnapshot
            if ($expectedName -in $archivedNames) {
                $manifestAssets.Add([ordered]@{
                    architecture = $architecture
                    name = $expectedName
                    path = "assets/$expectedName"
                    sha256 = $assetSnapshot.Sha256
                    size = $assetSnapshot.Length
                })
                $expectedArchivedAssetHashes.Add("assets/$expectedName", $assetSnapshot.Sha256)
            }
        }

        $signingStatusName = "$prefix-MSIX-SIGNING-STATUS.txt"
        $portableSnapshot = $architectureSnapshots["$prefix-portable.zip"]
        $signedPortableAsset = $previousUpdateAssets[$architecture]
        if ($signedPortableAsset.Name -cne "$prefix-portable.zip" -or
            $signedPortableAsset.Sha256 -cne $portableSnapshot.Sha256 -or
            $signedPortableAsset.Size -ne $portableSnapshot.Length) {
            throw "The signed previous update manifest does not match the verified Portable asset for $architecture."
        }
        if ($RequireSignedMsix) {
            $signingStatus = (Read-AgentDeskStableSnapshotText `
                -Snapshot $architectureSnapshots[$signingStatusName] `
                -MaximumCharacters 128).Trim()
            if ($signingStatus -cne "signed") {
                throw "Previous release MSIX for $architecture is not signed."
            }

            $verificationParameters = @{
                PackagePath = $architectureSnapshots["$prefix.msix"].Path
                ExpectedPackageName = "AgentDesk"
                ExpectedPackageVersion = $previousMsixVersion
                ExpectedArchitecture = $architecture
            }
            $verificationParameters.ExpectedThumbprint = $normalizedPreviousSignerThumbprint
            if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
                $verificationParameters.SignToolPath = $SignToolPath
            }
            & $signatureVerifier @verificationParameters
        }

        $checksumDocuments.Add([ordered]@{
            architecture = $architecture
            name = $checksumName
            path = "assets/$checksumName"
            sha256 = $checksumSnapshot.Sha256
        })
        $expectedArchivedAssetHashes.Add("assets/$checksumName", $checksumSnapshot.Sha256)
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        sourceRepository = $SourceRepository
        currentVersion = $CurrentVersion
        currentTag = $CurrentTag
        previousVersion = $PreviousVersion
        previousTag = $PreviousTag
        generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        architectures = @($Architectures | Sort-Object)
        assets = @($manifestAssets)
        checksumDocuments = @($checksumDocuments)
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $stageRoot "ROLLBACK-MANIFEST.json"),
        ($manifest | ConvertTo-Json -Depth 8) + "`n",
        [System.Text.UTF8Encoding]::new($false))

    $instructions = @"
# AgentDesk manual rollback

This archive contains the verified Windows x64 and ARM64 assets from AgentDesk
$PreviousTag. It is not an automatic updater and does not bypass Windows package
version rules.

1. Back up `%LOCALAPPDATA%\AgentDesk` before changing versions.
2. Verify this archive with its `.sha256` companion, then verify the selected
   asset against `assets/*-SHA256SUMS.txt`.
3. Portable: close AgentDesk, extract the previous Portable zip to a new folder,
   and start it from that folder. Do not overwrite a running installation.
4. MSIX: Windows does not install a lower package version over a higher one.
   Uninstall the current AgentDesk MSIX, then install the previous signed MSIX.
5. If rollback does not restore operation, keep the backup and report the failure
   with secrets, prompts, source text, usernames, and local paths removed.

Source release: $SourceRepository/releases/tag/$PreviousTag
"@
    [System.IO.File]::WriteAllText(
        (Join-Path $stageRoot "ROLLBACK-INSTRUCTIONS.md"),
        $instructions.Trim() + "`n",
        [System.Text.UTF8Encoding]::new($false))

    $instructionsZhCn = @"
# AgentDesk 手动回滚

本归档包含 AgentDesk $PreviousTag 已校验的 Windows x64 与 ARM64 产物。它不是自动
更新器，也不会绕过 Windows 的软件包版本规则。

1. 更改版本前备份 `%LOCALAPPDATA%\AgentDesk`。
2. 先使用同名 `.sha256` 文件校验本归档，再使用
   `assets/*-SHA256SUMS.txt` 校验所选安装文件。
3. Portable：关闭 AgentDesk，将旧版 Portable zip 解压到新目录并从该目录启动；
   不要覆盖正在运行的安装目录。
4. MSIX：Windows 不允许低版本覆盖高版本。先卸载当前 AgentDesk MSIX，再安装旧版
   已签名 MSIX。
5. 如果回滚后仍无法恢复，请保留备份，并在移除密钥、提示词、源码正文、用户名和
   本地路径后报告故障。

源码发布页：$SourceRepository/releases/tag/$PreviousTag
"@
    [System.IO.File]::WriteAllText(
        (Join-Path $stageRoot "ROLLBACK-INSTRUCTIONS.zh-CN.md"),
        $instructionsZhCn.Trim() + "`n",
        [System.Text.UTF8Encoding]::new($false))

    Remove-Item -LiteralPath $bundlePath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $bundlePath -CompressionLevel Optimal
    $verifiedBundle = $null
    try {
        $verifiedBundle = Open-VerifiedRollbackArchive `
            -Path $bundlePath `
            -ExpectedAssetHashes $expectedArchivedAssetHashes
        [System.IO.File]::WriteAllText(
            $bundleChecksumPath,
            "$($verifiedBundle.Sha256)  $bundleName`n",
            [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        if ($null -ne $verifiedBundle) {
            $verifiedBundle.Stream.Dispose()
        }
    }
}
finally {
    if ($null -ne $previousUpdateManifestBytes) {
        [System.Array]::Clear(
            $previousUpdateManifestBytes,
            0,
            $previousUpdateManifestBytes.Length)
    }
    foreach ($snapshot in $stableSnapshots) {
        Close-AgentDeskStableFileSnapshot -Snapshot $snapshot
    }
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $validationRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output $bundlePath
