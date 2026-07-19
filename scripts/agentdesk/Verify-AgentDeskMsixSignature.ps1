<#
.SYNOPSIS
Cryptographically verifies a signed AgentDesk MSIX and its publisher identity.

.PARAMETER PackagePath
Path to the signed MSIX package.

.PARAMETER ExpectedThumbprint
Optional repository-pinned SHA-1 thumbprint for the AgentDesk signing certificate.

.PARAMETER ExpectedPackageName
Repository-pinned MSIX package identity name.

.PARAMETER ExpectedPackageVersion
Exact four-part MSIX package identity version.

.PARAMETER ExpectedArchitecture
Exact MSIX ProcessorArchitecture value: x64 or arm64.

.PARAMETER SignToolPath
Optional explicit path to signtool.exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [ValidateSet("AgentDesk")][string]$ExpectedPackageName = "AgentDesk",
    [Parameter(Mandatory)][string]$ExpectedPackageVersion,
    [Parameter(Mandatory)][ValidateSet("x64", "arm64")][string]$ExpectedArchitecture,
    [string]$ExpectedThumbprint,
    [string]$SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$TrustedPublisher = "CN=AgentDesk"
$releaseFileSafetyModule = Join-Path $PSScriptRoot "AgentDesk.ReleaseFileSafety.psm1"
Import-Module -Name $releaseFileSafetyModule -Force -ErrorAction Stop

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Resolve-SignToolPath {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolvedPath = Get-FullPath $RequestedPath
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
            throw "signtool.exe was not found: $resolvedPath"
        }
        return $resolvedPath
    }

    $command = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $searchRoots = [System.Collections.Generic.List[string]]::new()
    foreach ($candidateRoot in @(
        $env:WindowsSdkVerBinPath,
        $env:WindowsSdkBinPath,
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"),
        (Join-Path $env:USERPROFILE ".nuget\packages\microsoft.windows.sdk.buildtools")
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidateRoot) -and
            (Test-Path -LiteralPath $candidateRoot -PathType Container)) {
            $searchRoots.Add((Get-FullPath $candidateRoot))
        }
    }

    $processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    $candidates = foreach ($root in $searchRoots | Select-Object -Unique) {
        Get-ChildItem -LiteralPath $root -Recurse -File -Filter "signtool.exe" -ErrorAction SilentlyContinue
    }
    $preferred = $candidates |
        Where-Object { $_.Directory.Name -eq $processArchitecture } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $preferred) {
        $preferred = $candidates | Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $preferred) {
        throw "signtool.exe was not found on PATH, in the Windows SDK, or in the restored Windows SDK BuildTools package."
    }
    return $preferred.FullName
}

function Get-PackageManifestIdentity {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $manifestEntry = $archive.GetEntry("AppxManifest.xml")
        if ($null -eq $manifestEntry -or
            $manifestEntry.Length -le 0 -or
            $manifestEntry.Length -gt 1024 * 1024) {
            throw "The MSIX package has no valid AppxManifest.xml."
        }

        $reader = [System.IO.StreamReader]::new(
            $manifestEntry.Open(),
            [System.Text.UTF8Encoding]::new($false, $true),
            $true,
            4096,
            $false)
        try {
            [xml]$manifest = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $identity = $manifest.Package.Identity
        $name = [string]$identity.Name
        $publisher = [string]$identity.Publisher
        $version = [string]$identity.Version
        $architecture = [string]$identity.ProcessorArchitecture
        if ([string]::IsNullOrWhiteSpace($name) -or
            [string]::IsNullOrWhiteSpace($publisher) -or
            [string]::IsNullOrWhiteSpace($version) -or
            [string]::IsNullOrWhiteSpace($architecture)) {
            throw "The MSIX AppxManifest.xml package identity is incomplete."
        }
        return [pscustomobject]@{
            Name = $name.Trim()
            Publisher = $publisher.Trim()
            Version = $version.Trim()
            Architecture = $architecture.Trim().ToLowerInvariant()
        }
    }
    catch [System.IO.InvalidDataException] {
        throw "The MSIX package is not a valid archive."
    }
    catch [System.Xml.XmlException] {
        throw "The MSIX AppxManifest.xml is malformed."
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

$PackagePath = Get-FullPath $PackagePath
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Signed MSIX package was not found: $PackagePath"
}
$packageInformation = Get-Item -LiteralPath $PackagePath -Force
if ($packageInformation.Length -le 0 -or
    $packageInformation.Length -gt 512L * 1024 * 1024 -or
    ($packageInformation.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Signed MSIX package is empty, oversized, or a reparse point: $PackagePath"
}

$normalizedExpectedThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    $normalizedExpectedThumbprint = $ExpectedThumbprint.Trim().ToUpperInvariant()
    if ($normalizedExpectedThumbprint -notmatch '^[0-9A-F]{40}$') {
        throw "The expected MSIX signer thumbprint must contain exactly 40 hexadecimal characters."
    }
}

if ($ExpectedPackageVersion -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}\.\d{1,5}$') {
    throw "The expected MSIX package Version must contain four numeric components."
}
try {
    $parsedExpectedVersion = [version]$ExpectedPackageVersion
}
catch {
    throw "The expected MSIX package Version is invalid."
}
if (@(
        $parsedExpectedVersion.Major,
        $parsedExpectedVersion.Minor,
        $parsedExpectedVersion.Build,
        $parsedExpectedVersion.Revision
    ) | Where-Object { $_ -lt 0 -or $_ -gt 65535 }) {
    throw "The expected MSIX package Version components must be between 0 and 65535."
}

$resolvedSignTool = Resolve-SignToolPath $SignToolPath
$verificationStageRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-msix-verify-" + [guid]::NewGuid().ToString("N"))
$packageSnapshot = $null
try {
    $packageSnapshot = New-AgentDeskStableFileSnapshot `
        -SourcePath $PackagePath `
        -StageRoot $verificationStageRoot `
        -FileName "package.msix" `
        -MaximumBytes (512L * 1024 * 1024) `
        -Description "signed MSIX package"

    $verifiedPackagePath = $packageSnapshot.Path
    Write-Host "> $resolvedSignTool verify /all /pa /v $verifiedPackagePath"
    & $resolvedSignTool verify /all /pa /v $verifiedPackagePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe rejected the MSIX signature with exit code $LASTEXITCODE."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $verifiedPackagePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "The MSIX Authenticode signature is not valid: $($signature.Status) $($signature.StatusMessage)"
    }

    $manifestIdentity = Get-PackageManifestIdentity -Path $verifiedPackagePath
    $manifestPublisher = $manifestIdentity.Publisher
    if (-not [System.String]::Equals(
            $manifestIdentity.Name,
            $ExpectedPackageName,
            [System.StringComparison]::Ordinal)) {
        throw "MSIX package Name '$($manifestIdentity.Name)' does not match expected package Name '$ExpectedPackageName'."
    }
    if (-not [System.String]::Equals(
            $manifestIdentity.Version,
            $ExpectedPackageVersion,
            [System.StringComparison]::Ordinal)) {
        throw "MSIX package Version '$($manifestIdentity.Version)' does not match expected package Version '$ExpectedPackageVersion'."
    }
    if (-not [System.String]::Equals(
            $manifestIdentity.Architecture,
            $ExpectedArchitecture,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX package architecture '$($manifestIdentity.Architecture)' does not match expected package architecture '$ExpectedArchitecture'."
    }
    if (-not [System.String]::Equals(
            $manifestPublisher,
            $TrustedPublisher,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX manifest Publisher '$manifestPublisher' does not match repository-trusted Publisher '$TrustedPublisher'."
    }

    $actualPublisher = $signature.SignerCertificate.Subject.Trim()
    if (-not [System.String]::Equals(
            $actualPublisher,
            $TrustedPublisher,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX signer Subject '$actualPublisher' does not match repository-trusted Publisher '$TrustedPublisher'."
    }

    if ($null -ne $normalizedExpectedThumbprint) {
        $actualThumbprint = [string]$signature.SignerCertificate.Thumbprint
        if (-not [System.String]::Equals(
                $actualThumbprint.Trim(),
                $normalizedExpectedThumbprint,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "MSIX signer thumbprint '$actualThumbprint' does not match the repository-pinned thumbprint."
        }
    }

    if (-not [System.String]::Equals(
            $actualPublisher,
            $manifestPublisher,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSIX signer Subject '$actualPublisher' does not match manifest Publisher '$manifestPublisher'."
    }

    Write-Host "Verified MSIX signer: $actualPublisher"
}
finally {
    if ($null -ne $packageSnapshot) {
        Close-AgentDeskStableFileSnapshot -Snapshot $packageSnapshot
    }
    Remove-Item -LiteralPath $verificationStageRoot -Recurse -Force -ErrorAction SilentlyContinue
}
