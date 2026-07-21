<#
.SYNOPSIS
Creates deterministic AgentDesk update metadata and an ECDSA P-256 detached signature.

.DESCRIPTION
Builds strict manifests for the x64/arm64 portable release archives and the
independent updater archives. The private PKCS#8 key must come from a file
under a temporary directory or from a named process environment variable
containing Base64-encoded PKCS#8 DER bytes. The supplied SPKI public key is used
to verify both generated DER signatures before any metadata is published.
Private signing material is never copied or printed.

.PARAMETER X64UpdaterPath
Final x64 updater zip containing AgentDesk.Updater.exe.

.PARAMETER Arm64UpdaterPath
Final arm64 updater zip containing AgentDesk.Updater.exe.

.PARAMETER PrivateKeyPath
PKCS#8 DER private key file under the system or runner temporary directory.

.PARAMETER PrivateKeyEnvironmentVariable
Name of a process environment variable containing Base64-encoded PKCS#8 DER.
Specify exactly one private-key source.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$Repository,

    [Parameter(Mandatory)]
    [string]$Tag,

    [Parameter(Mandatory)]
    [string]$X64PackagePath,

    # Optional when publishing an x64-only self-hosted feed.
    [string]$Arm64PackagePath,

    [Parameter(Mandatory)]
    [string]$X64UpdaterPath,

    # Optional when publishing an x64-only self-hosted feed.
    [string]$Arm64UpdaterPath,

    [Parameter(Mandatory)]
    [string]$PublicKeyPath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$PrivateKeyPath,
    [string]$PrivateKeyEnvironmentVariable,

    # Optional HTTPS base used for asset URLs instead of GitHub releases.
    # Example: https://update.rkshadow.com/releases/v0.1.0-alpha.6
    [string]$AssetBaseUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$releaseFileSafetyModule = Join-Path $PSScriptRoot "AgentDesk.ReleaseFileSafety.psm1"
Import-Module -Name $releaseFileSafetyModule -Force -ErrorAction Stop

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Test-IsWindowsHost {
    if (Get-Variable -Name IsWindows -Scope Global -ErrorAction SilentlyContinue) {
        return [bool]$IsWindows
    }
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Test-IsContained {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $comparison = if (Test-IsWindowsHost) {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }
    $fullRoot = (Get-FullPath $Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullCandidate = Get-FullPath $Candidate
    return $fullCandidate.StartsWith(
        $fullRoot + [System.IO.Path]::DirectorySeparatorChar,
        $comparison)
}

function Assert-NoReparsePathSegments {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Candidate
    )

    $comparison = if (Test-IsWindowsHost) {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }
    $fullRoot = (Get-FullPath $Root).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $currentPath = Get-FullPath $Candidate
    if (-not (Test-IsContained -Root $fullRoot -Candidate $currentPath)) {
        throw "The private key file must be stored under a temporary directory."
    }

    while ($true) {
        $information = Get-Item -LiteralPath $currentPath -Force -ErrorAction SilentlyContinue
        if ($null -eq $information) {
            throw "The private key path is missing or invalid."
        }
        if (($information.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The private key path must not contain a reparse point."
        }
        if ([System.String]::Equals($currentPath, $fullRoot, $comparison)) {
            break
        }
        $parent = [System.IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) {
            throw "The private key path escapes its temporary directory."
        }
        $currentPath = $parent.FullName.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    }
}

function Read-SafeFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][long]$MinimumBytes,
        [Parameter(Mandatory)][long]$MaximumBytes
    )

    $fullPath = Get-FullPath $Path
    $information = Get-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    if ($null -eq $information -or
        $information.PSIsContainer -or
        $information.Length -lt $MinimumBytes -or
        $information.Length -gt $MaximumBytes -or
        ($information.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The $Description file is missing or invalid: $fullPath"
    }

    $bytes = [System.IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -ne $information.Length) {
        [System.Array]::Clear($bytes, 0, $bytes.Length)
        throw "The $Description file changed while it was being read: $fullPath"
    }
    return ,$bytes
}

function Assert-P256Key {
    param(
        [Parameter(Mandatory)][System.Security.Cryptography.ECDsa]$Key,
        [Parameter(Mandatory)][string]$Description
    )

    $parameters = $Key.ExportParameters($false)
    if ($Key.KeySize -ne 256 -or
        $parameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7") {
        throw "The $Description must be an ECDSA P-256 key."
    }
}

function Assert-PeStreamArchitecture {
    param(
        [Parameter(Mandatory)][System.IO.Stream]$Stream,
        [Parameter(Mandatory)][long]$Length,
        [Parameter(Mandatory)][ValidateSet("x64", "arm64")][string]$Architecture
    )

    $expectedMachine = if ($Architecture -eq "x64") { 0x8664 } else { 0xaa64 }
    if ($Length -lt 0x86) {
        throw "The $Architecture updater is not a valid PE executable."
    }

    $dosHeader = [byte[]]::new(0x40)
    $read = 0
    while ($read -lt $dosHeader.Length) {
        $count = $Stream.Read($dosHeader, $read, $dosHeader.Length - $read)
        if ($count -le 0) {
            throw "The $Architecture updater is not a valid PE executable."
        }
        $read += $count
    }
    if ([BitConverter]::ToUInt16($dosHeader, 0) -ne 0x5a4d) {
        throw "The $Architecture updater is not a valid PE executable."
    }

    $peOffset = [BitConverter]::ToInt32($dosHeader, 0x3c)
    if ($peOffset -lt 0x40 -or $peOffset -gt $Length - 6 -or $peOffset -gt 1024 * 1024) {
        throw "The $Architecture updater has an invalid PE header offset."
    }

    $remaining = $peOffset - $dosHeader.Length
    $discard = [byte[]]::new(4096)
    while ($remaining -gt 0) {
        $count = $Stream.Read($discard, 0, [Math]::Min($discard.Length, $remaining))
        if ($count -le 0) {
            throw "The $Architecture updater has an invalid PE header offset."
        }
        $remaining -= $count
    }

    $peHeader = [byte[]]::new(6)
    $read = 0
    while ($read -lt $peHeader.Length) {
        $count = $Stream.Read($peHeader, $read, $peHeader.Length - $read)
        if ($count -le 0) {
            throw "The $Architecture updater has an invalid PE signature."
        }
        $read += $count
    }
    if ([BitConverter]::ToUInt32($peHeader, 0) -ne 0x00004550) {
        throw "The $Architecture updater has an invalid PE signature."
    }
    if ([BitConverter]::ToUInt16($peHeader, 4) -ne $expectedMachine) {
        throw "The $Architecture updater PE architecture does not match its release label."
    }
}

function Assert-UpdaterArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][ValidateSet("x64", "arm64")][string]$Architecture
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $entries = @($archive.Entries)
        if ($entries.Count -ne 1 -or
            $entries[0].FullName -cne "AgentDesk.Updater.exe" -or
            $entries[0].Length -le 0 -or
            $entries[0].Length -gt 256L * 1024 * 1024) {
            throw "The $Architecture updater archive must contain exactly AgentDesk.Updater.exe."
        }

        $stream = $entries[0].Open()
        try {
            Assert-PeStreamArchitecture `
                -Stream $stream `
                -Length $entries[0].Length `
                -Architecture $Architecture
        }
        finally {
            $stream.Dispose()
        }
    }
    catch [System.IO.InvalidDataException] {
        throw "The $Architecture updater archive is not a valid zip file."
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

function New-ManifestBytes {
    param(
        [Parameter(Mandatory)][string]$Product,
        [Parameter(Mandatory)][string]$ManifestVersion,
        [Parameter(Mandatory)][object[]]$Assets,
        [Parameter(Mandatory)][string]$ManifestRepository,
        [Parameter(Mandatory)][string]$ManifestTag,
        [string]$EntryPoint,
        [string]$AssetBaseUrl
    )

    $memory = [System.IO.MemoryStream]::new()
    $writer = $null
    try {
        $options = [System.Text.Json.JsonWriterOptions]::new()
        $options.Indented = $false
        $options.SkipValidation = $false
        $writer = [System.Text.Json.Utf8JsonWriter]::new($memory, $options)
        $writer.WriteStartObject()
        $writer.WriteNumber("schemaVersion", 1)
        $writer.WriteString("product", $Product)
        $writer.WriteString("version", $ManifestVersion)
        $writer.WriteStartArray("assets")
        foreach ($asset in $Assets) {
            if (-not [string]::IsNullOrWhiteSpace($AssetBaseUrl)) {
                $url = ($AssetBaseUrl.TrimEnd('/') + '/' + $asset.Name)
            }
            else {
                $url = "https://github.com/$ManifestRepository/releases/download/$ManifestTag/$($asset.Name)"
            }
            $writer.WriteStartObject()
            $writer.WriteString("architecture", $asset.Architecture)
            $writer.WriteString("url", $url)
            $writer.WriteString("sha256", $asset.Sha256)
            $writer.WriteNumber("size", [long]$asset.Length)
            if (-not [string]::IsNullOrWhiteSpace($EntryPoint)) {
                $writer.WriteString("entryPoint", $EntryPoint)
            }
            $writer.WriteEndObject()
        }
        $writer.WriteEndArray()
        $writer.WriteEndObject()
        $writer.Flush()
        return ,$memory.ToArray()
    }
    finally {
        if ($null -ne $writer) {
            $writer.Dispose()
        }
        $memory.Dispose()
    }
}

function Write-OutputFile {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][byte[]]$Bytes
    )

    $path = Join-Path $Root $Name
    if (-not (Test-IsContained -Root $Root -Candidate $path)) {
        throw "The update metadata output path escapes its release directory."
    }
    if (Test-Path -LiteralPath $path) {
        $attributes = [System.IO.File]::GetAttributes($path)
        if (($attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to overwrite a reparse-point update metadata file: $path"
        }
    }
    [System.IO.File]::WriteAllBytes($path, $Bytes)
    return $path
}

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:ci|alpha|beta|preview|rc)\.(?:0|[1-9]\d*))?$') {
    throw "The update manifest version must be a supported stable or numbered prerelease version."
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw "The update manifest repository must use the owner/name form."
}
if ($Tag -cne "v$Version") {
    throw "The update manifest tag must exactly match v<version>."
}
if (-not [string]::IsNullOrWhiteSpace($AssetBaseUrl)) {
    $assetBaseUri = $null
    if (-not [Uri]::TryCreate($AssetBaseUrl, [UriKind]::Absolute, [ref]$assetBaseUri)) {
        throw "AssetBaseUrl must be an absolute HTTPS URI."
    }
    if ($assetBaseUri.Scheme -cne [Uri]::UriSchemeHttps -or
        -not $assetBaseUri.IsDefaultPort -or
        -not [string]::IsNullOrEmpty($assetBaseUri.UserInfo) -or
        -not [string]::IsNullOrEmpty($assetBaseUri.Query) -or
        -not [string]::IsNullOrEmpty($assetBaseUri.Fragment)) {
        throw "AssetBaseUrl must be a plain HTTPS origin/path without credentials, query, or fragment."
    }
}

$hasPrivateKeyPath = -not [string]::IsNullOrWhiteSpace($PrivateKeyPath)
$hasPrivateKeyEnvironment = -not [string]::IsNullOrWhiteSpace($PrivateKeyEnvironmentVariable)
if ($hasPrivateKeyPath -eq $hasPrivateKeyEnvironment) {
    throw "Specify exactly one private key source: a temporary file or a protected environment variable."
}

$privateKeyBytes = $null
$publicKeyBytes = $null
$signer = $null
$verifier = $null
$releaseSnapshots = [System.Collections.Generic.List[object]]::new()
$releaseStageRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-update-manifest-stage-" + [guid]::NewGuid().ToString("N"))
try {
    if ($hasPrivateKeyPath) {
        $fullPrivateKeyPath = Get-FullPath $PrivateKeyPath
        $temporaryRoots = @([System.IO.Path]::GetTempPath())
        if (-not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
            $temporaryRoots += $env:RUNNER_TEMP
        }
        $matchedTemporaryRoot = $temporaryRoots |
            Where-Object { Test-IsContained -Root $_ -Candidate $fullPrivateKeyPath } |
            Select-Object -First 1
        if ($null -eq $matchedTemporaryRoot) {
            throw "The private key file must be stored under a temporary directory."
        }
        Assert-NoReparsePathSegments `
            -Root $matchedTemporaryRoot `
            -Candidate $fullPrivateKeyPath
        $privateKeyBytes = Read-SafeFile `
            -Path $fullPrivateKeyPath `
            -Description "private key" `
            -MinimumBytes 32 `
            -MaximumBytes 4096
    }
    else {
        if ($PrivateKeyEnvironmentVariable -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,127}$') {
            throw "The private key environment variable name is invalid."
        }
        $encodedPrivateKey = [Environment]::GetEnvironmentVariable(
            $PrivateKeyEnvironmentVariable,
            [EnvironmentVariableTarget]::Process)
        if ([string]::IsNullOrWhiteSpace($encodedPrivateKey) -or
            $encodedPrivateKey.Length -gt 16 * 1024) {
            throw "The protected private key environment variable is missing or invalid."
        }
        try {
            $privateKeyBytes = [Convert]::FromBase64String($encodedPrivateKey)
        }
        catch [FormatException] {
            throw "The protected private key environment variable is not valid Base64."
        }
        if ($privateKeyBytes.Length -lt 32 -or $privateKeyBytes.Length -gt 4096) {
            throw "The protected private key environment variable has an invalid size."
        }
    }

    $publicKeyBytes = Read-SafeFile `
        -Path $PublicKeyPath `
        -Description "public key" `
        -MinimumBytes 32 `
        -MaximumBytes 1024

    $x64FullPath = Get-FullPath $X64PackagePath
    $x64UpdaterFullPath = Get-FullPath $X64UpdaterPath
    $expectedX64Name = "AgentDesk-$Version-win-x64-portable.zip"
    $expectedX64UpdaterName = "AgentDesk-$Version-win-x64-updater.zip"
    $releaseAssets = [System.Collections.Generic.List[object]]::new()
    $releaseAssets.Add(@{
            Architecture = "x64"
            Kind = "package"
            Path = $x64FullPath
            Name = $expectedX64Name
            Description = "x64 portable update package"
        }) | Out-Null
    $releaseAssets.Add(@{
            Architecture = "x64"
            Kind = "updater"
            Path = $x64UpdaterFullPath
            Name = $expectedX64UpdaterName
            Description = "x64 updater asset"
        }) | Out-Null

    $hasArm64Package = -not [string]::IsNullOrWhiteSpace($Arm64PackagePath)
    $hasArm64Updater = -not [string]::IsNullOrWhiteSpace($Arm64UpdaterPath)
    if ($hasArm64Package -ne $hasArm64Updater) {
        throw "Provide both Arm64PackagePath and Arm64UpdaterPath, or omit both for an x64-only feed."
    }
    if ($hasArm64Package) {
        $arm64FullPath = Get-FullPath $Arm64PackagePath
        $arm64UpdaterFullPath = Get-FullPath $Arm64UpdaterPath
        $expectedArm64Name = "AgentDesk-$Version-win-arm64-portable.zip"
        $expectedArm64UpdaterName = "AgentDesk-$Version-win-arm64-updater.zip"
        $releaseAssets.Add(@{
                Architecture = "arm64"
                Kind = "package"
                Path = $arm64FullPath
                Name = $expectedArm64Name
                Description = "arm64 portable update package"
            }) | Out-Null
        $releaseAssets.Add(@{
                Architecture = "arm64"
                Kind = "updater"
                Path = $arm64UpdaterFullPath
                Name = $expectedArm64UpdaterName
                Description = "arm64 updater asset"
            }) | Out-Null
    }
    foreach ($asset in $releaseAssets) {
        $information = Get-Item -LiteralPath $asset.Path -Force -ErrorAction SilentlyContinue
        if ($null -eq $information -or
            $information.PSIsContainer -or
            $information.Name -cne $asset.Name -or
            $information.Length -le 0 -or
            $information.Length -gt 512L * 1024 * 1024 -or
            ($information.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The $($asset.Description) is missing or invalid."
        }

        $snapshot = New-AgentDeskStableFileSnapshot `
            -SourcePath $asset.Path `
            -StageRoot $releaseStageRoot `
            -FileName $asset.Name `
            -MaximumBytes (512L * 1024 * 1024) `
            -Description $asset.Description
        $releaseSnapshots.Add($snapshot)
        $asset.Snapshot = $snapshot
    }
    $x64UpdaterSnapshot = ($releaseAssets |
        Where-Object { $_.Kind -ceq "updater" -and $_.Architecture -ceq "x64" }).Snapshot
    Assert-UpdaterArchive -Path $x64UpdaterSnapshot.Path -Architecture "x64"
    $arm64UpdaterSnapshot = ($releaseAssets |
        Where-Object { $_.Kind -ceq "updater" -and $_.Architecture -ceq "arm64" } |
        Select-Object -First 1)
    if ($null -ne $arm64UpdaterSnapshot) {
        Assert-UpdaterArchive -Path $arm64UpdaterSnapshot.Snapshot.Path -Architecture "arm64"
    }

    $signer = [System.Security.Cryptography.ECDsa]::Create()
    $privateBytesRead = 0
    $signer.ImportPkcs8PrivateKey($privateKeyBytes, [ref]$privateBytesRead)
    if ($privateBytesRead -ne $privateKeyBytes.Length) {
        throw "The private key file contains trailing or unsupported data."
    }
    Assert-P256Key -Key $signer -Description "private signing key"

    $verifier = [System.Security.Cryptography.ECDsa]::Create()
    $publicBytesRead = 0
    $verifier.ImportSubjectPublicKeyInfo($publicKeyBytes, [ref]$publicBytesRead)
    if ($publicBytesRead -ne $publicKeyBytes.Length) {
        throw "The public key file contains trailing or unsupported data."
    }
    Assert-P256Key -Key $verifier -Description "public verification key"

    $packageAssets = @($releaseAssets |
        Where-Object Kind -CEQ "package" |
        ForEach-Object {
            @{
                Architecture = $_.Architecture
                Name = $_.Name
                Sha256 = $_.Snapshot.Sha256
                Length = $_.Snapshot.Length
            }
        })
    $updaterAssets = @($releaseAssets |
        Where-Object Kind -CEQ "updater" |
        ForEach-Object {
            @{
                Architecture = $_.Architecture
                Name = $_.Name
                Sha256 = $_.Snapshot.Sha256
                Length = $_.Snapshot.Length
            }
        })
    $manifestBytes = New-ManifestBytes `
        -Product "AgentDesk" `
        -ManifestVersion $Version `
        -Assets $packageAssets `
        -ManifestRepository $Repository `
        -ManifestTag $Tag `
        -EntryPoint "AgentDesk.App.exe" `
        -AssetBaseUrl $AssetBaseUrl
    $updaterManifestBytes = New-ManifestBytes `
        -Product "AgentDesk.Updater" `
        -ManifestVersion $Version `
        -Assets $updaterAssets `
        -ManifestRepository $Repository `
        -ManifestTag $Tag `
        -EntryPoint "AgentDesk.Updater.exe" `
        -AssetBaseUrl $AssetBaseUrl

    $signatureBytes = $signer.SignData(
        $manifestBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    $updaterSignatureBytes = $signer.SignData(
        $updaterManifestBytes,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    foreach ($signedMetadata in @(
        @{ Bytes = $manifestBytes; Signature = $signatureBytes },
        @{ Bytes = $updaterManifestBytes; Signature = $updaterSignatureBytes }
    )) {
        if (-not $verifier.VerifyData(
                $signedMetadata.Bytes,
                $signedMetadata.Signature,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
            throw "The private signing key does not match the supplied public verification key."
        }
    }

    $outputRoot = Get-FullPath $OutputDirectory
    [System.IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    if (([System.IO.File]::GetAttributes($outputRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The update metadata output directory must not be a reparse point."
    }

    $manifestName = "AgentDesk-update-manifest.json"
    $signatureName = "AgentDesk-update-manifest.json.sig"
    $updaterManifestName = "AgentDesk-updater-manifest.json"
    $updaterSignatureName = "AgentDesk-updater-manifest.json.sig"
    $publicKeyName = "AgentDesk-update-public-key.spki"
    $manifestPath = Write-OutputFile -Root $outputRoot -Name $manifestName -Bytes $manifestBytes
    Write-OutputFile -Root $outputRoot -Name $signatureName -Bytes $signatureBytes | Out-Null
    Write-OutputFile -Root $outputRoot -Name $updaterManifestName -Bytes $updaterManifestBytes | Out-Null
    Write-OutputFile -Root $outputRoot -Name $updaterSignatureName -Bytes $updaterSignatureBytes | Out-Null
    Write-OutputFile -Root $outputRoot -Name $publicKeyName -Bytes $publicKeyBytes | Out-Null

    $checksumLines = foreach ($name in @(
        $manifestName,
        $signatureName,
        $updaterManifestName,
        $updaterSignatureName,
        $publicKeyName
    ) | Sort-Object) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $outputRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    $checksumBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        ($checksumLines -join "`n") + "`n")
    Write-OutputFile `
        -Root $outputRoot `
        -Name "AgentDesk-update-metadata-SHA256SUMS.txt" `
        -Bytes $checksumBytes | Out-Null

    Write-Output $manifestPath
}
catch [System.Security.Cryptography.CryptographicException] {
    throw "The update signing key material is invalid. $($_.Exception.Message)"
}
finally {
    if ($null -ne $signer) {
        $signer.Dispose()
    }
    if ($null -ne $verifier) {
        $verifier.Dispose()
    }
    if ($null -ne $privateKeyBytes) {
        [System.Array]::Clear($privateKeyBytes, 0, $privateKeyBytes.Length)
    }
    if ($null -ne $publicKeyBytes) {
        [System.Array]::Clear($publicKeyBytes, 0, $publicKeyBytes.Length)
    }
    foreach ($snapshot in $releaseSnapshots) {
        Close-AgentDeskStableFileSnapshot -Snapshot $snapshot
    }
    Remove-Item -LiteralPath $releaseStageRoot -Recurse -Force -ErrorAction SilentlyContinue
}
