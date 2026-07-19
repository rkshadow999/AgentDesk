<#
.SYNOPSIS
Installs a pinned protoc distribution and exposes it to later CI steps.

.DESCRIPTION
Downloads the official protobuf release archive for Windows x64 (also used
under Windows ARM64 emulation), Linux x64, or Linux ARM64, verifies its SHA-256 digest, and
sets PROTOC/GITHUB_PATH when running in GitHub Actions.

.PARAMETER Version
The protobuf release version. AgentDesk CI currently supports 29.3.

.PARAMETER Destination
Directory that receives the protoc bin and include folders.
#>
[CmdletBinding()]
param(
    [ValidateSet("29.3")]
    [string]$Version = "29.3",

    [Parameter(Mandatory)]
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
$isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
$isLinuxPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Linux)
if ($isWindowsPlatform) {
    if ($architecture -notin @(
        [System.Runtime.InteropServices.Architecture]::X64,
        [System.Runtime.InteropServices.Architecture]::Arm64
    )) {
        throw "Unsupported Windows architecture for protoc: $architecture"
    }
    $asset = "protoc-$Version-win64.zip"
    $expectedSha256 = "57ea59e9f551ad8d71ffaa9b5cfbe0ca1f4e720972a1db7ec2d12ab44bff9383"
    $protocRelativePath = "bin/protoc.exe"
}
elseif ($isLinuxPlatform) {
    if ($architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        $asset = "protoc-$Version-linux-x86_64.zip"
        $expectedSha256 = "3e866620c5be27664f3d2fa2d656b5f3e09b5152b42f1bedbf427b333e90021a"
    }
    elseif ($architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
        $asset = "protoc-$Version-linux-aarch_64.zip"
        $expectedSha256 = "6427349140e01f06e049e707a58709a4f221ae73ab9a0425bc4a00c8d0e1ab32"
    }
    else {
        throw "Unsupported Linux architecture for protoc: $architecture"
    }
    $protocRelativePath = "bin/protoc"
}
else {
    throw "Unsupported platform for the pinned protoc distribution: $architecture"
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
[System.IO.Directory]::CreateDirectory($destinationPath) | Out-Null
$archivePath = Join-Path $destinationPath $asset
$downloadUri = "https://github.com/protocolbuffers/protobuf/releases/download/v$Version/$asset"

Write-Host "Downloading $downloadUri"
Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath
$actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw "protoc checksum mismatch. Expected $expectedSha256, got $actualSha256."
}

Expand-Archive -LiteralPath $archivePath -DestinationPath $destinationPath -Force
Remove-Item -LiteralPath $archivePath -Force
$protocPath = Join-Path $destinationPath $protocRelativePath
if (-not (Test-Path -LiteralPath $protocPath -PathType Leaf)) {
    throw "protoc was not installed at the expected path: $protocPath"
}

if ($isLinuxPlatform) {
    & chmod +x -- $protocPath
    if ($LASTEXITCODE -ne 0) {
        throw "chmod failed for $protocPath"
    }
}

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
if ($env:GITHUB_ENV) {
    [System.IO.File]::AppendAllText($env:GITHUB_ENV, "PROTOC=$protocPath`n", $utf8NoBom)
}
if ($env:GITHUB_PATH) {
    [System.IO.File]::AppendAllText(
        $env:GITHUB_PATH,
        "$(Split-Path -Parent $protocPath)`n",
        $utf8NoBom)
}

Write-Output $protocPath
