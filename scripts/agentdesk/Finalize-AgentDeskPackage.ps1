<#
.SYNOPSIS
Creates final AgentDesk archives, SBOM companions, and SHA-256 checksums.

.DESCRIPTION
Consumes the package-input directory produced by Build-AgentDeskPackage.ps1
after SBOM generation, creates the portable zip, flattens the MSIX output, and
writes SHA256SUMS.txt for every release file.

.PARAMETER Architecture
Windows package architecture: x64 or arm64.

.PARAMETER PackageInputRoot
Directory containing portable, msix, and update-staging subdirectories.

.PARAMETER OutputRoot
Parent directory for the final release directory.

.PARAMETER DryRun
Prints resolved paths without changing files.
#>
[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",

    [string]$Version = "0.1.0-ci.0",

    [Parameter(Mandatory)]
    [string]$PackageInputRoot,

    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Reset-ChildDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$AllowedRoot
    )
    $fullPath = Get-FullPath $Path
    $fullRoot = (Get-FullPath $AllowedRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $requiredPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the release root: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($fullPath) | Out-Null
    return $fullPath
}

$PackageInputRoot = Get-FullPath $PackageInputRoot
$OutputRoot = Get-FullPath $OutputRoot
$portableDirectory = Join-Path $PackageInputRoot "portable"
$msixDirectory = Join-Path $PackageInputRoot "msix"
$updateStagingDirectory = Join-Path $PackageInputRoot "update-staging"
$safeVersion = ($Version -replace '[^0-9A-Za-z._-]', '-')
$releaseDirectory = Join-Path $OutputRoot "AgentDesk-$safeVersion-win-$Architecture"

Write-Host "Portable input: $portableDirectory"
Write-Host "MSIX input: $msixDirectory"
Write-Host "Updater input: $updateStagingDirectory"
Write-Host "Release output: $releaseDirectory"
if ($DryRun) {
    Write-Output $releaseDirectory
    return
}

foreach ($requiredPath in @($portableDirectory, $msixDirectory, $updateStagingDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Container)) {
        throw "Package input directory is missing: $requiredPath"
    }
}
foreach ($sbomName in @("SBOM.spdx.json", "SBOM.cyclonedx.json")) {
    if (-not (Test-Path -LiteralPath (Join-Path $portableDirectory $sbomName) -PathType Leaf)) {
        throw "Required SBOM is missing: $sbomName"
    }
}

[System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$releaseDirectory = Reset-ChildDirectory -Path $releaseDirectory -AllowedRoot $OutputRoot
$portableZip = Join-Path $releaseDirectory "AgentDesk-$safeVersion-win-$Architecture-portable.zip"
Compress-Archive -Path (Join-Path $portableDirectory "*") -DestinationPath $portableZip -CompressionLevel Optimal

$stagedUpdater = Join-Path $updateStagingDirectory "AgentDesk.Updater.exe"
if (-not (Test-Path -LiteralPath $stagedUpdater -PathType Leaf) -or
    (Get-Item -LiteralPath $stagedUpdater).Length -le 0) {
    throw "The staged AgentDesk updater executable is missing or empty: $stagedUpdater"
}
$releaseUpdater = Join-Path $releaseDirectory "AgentDesk-$safeVersion-win-$Architecture-updater.zip"
Compress-Archive `
    -Path $stagedUpdater `
    -DestinationPath $releaseUpdater `
    -CompressionLevel Optimal

$developmentStatus = Join-Path $updateStagingDirectory "DEVELOPMENT-ONLY.txt"
if (-not (Test-Path -LiteralPath $developmentStatus -PathType Leaf)) {
    throw "The updater development-only status marker is missing: $developmentStatus"
}
$releaseUpdateStatus = Join-Path $releaseDirectory "AgentDesk-$safeVersion-win-$Architecture-UPDATE-STATUS.txt"
Copy-Item -LiteralPath $developmentStatus -Destination $releaseUpdateStatus -Force

$msix = Get-ChildItem -LiteralPath $msixDirectory -File -Filter "AgentDesk-*.msix" |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if (-not $msix) {
    throw "No stable AgentDesk MSIX was found under $msixDirectory"
}
Copy-Item -LiteralPath $msix.FullName -Destination (Join-Path $releaseDirectory $msix.Name) -Force

$sbomMappings = @{
    "SBOM.spdx.json" = "AgentDesk-$safeVersion-win-$Architecture.spdx.json"
    "SBOM.cyclonedx.json" = "AgentDesk-$safeVersion-win-$Architecture.cyclonedx.json"
}
foreach ($entry in $sbomMappings.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $portableDirectory $entry.Key) -Destination (Join-Path $releaseDirectory $entry.Value) -Force
}

$signingStatusPath = Join-Path $msixDirectory "MSIX-SIGNING-STATUS.txt"
if (Test-Path -LiteralPath $signingStatusPath -PathType Leaf) {
    $releaseSigningStatusName = "AgentDesk-$safeVersion-win-$Architecture-MSIX-SIGNING-STATUS.txt"
    Copy-Item -LiteralPath $signingStatusPath -Destination (Join-Path $releaseDirectory $releaseSigningStatusName) -Force
}

$checksumLines = Get-ChildItem -LiteralPath $releaseDirectory -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
$checksumFileName = "AgentDesk-$safeVersion-win-$Architecture-SHA256SUMS.txt"
[System.IO.File]::WriteAllLines(
    (Join-Path $releaseDirectory $checksumFileName),
    [string[]]$checksumLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Output $releaseDirectory
