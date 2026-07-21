<#
.SYNOPSIS
Signs AgentDesk Portable update metadata and publishes it to the self-hosted
update origin on update.rkshadow.com (server 74).

.DESCRIPTION
Takes an existing x64 Portable release directory (from Finalize-AgentDeskPackage
or a release-alpha folder), signs manifests with the community ECDSA P-256 key,
uploads feed + release assets over SSH/SCP, and writes an optional Windows
installer package under artifacts.

Private key material is never printed. Default private key path is under the
user profile (~/.agentdesk), copied into %TEMP% only for the signing script.

.PARAMETER Version
Release version string, e.g. 0.1.0-alpha.6

.PARAMETER ReleaseDirectory
Directory containing AgentDesk-<version>-win-x64-portable.zip and
AgentDesk-<version>-win-x64-updater.zip

.PARAMETER SshTarget
SSH target for the update origin, default root@74.211.104.202

.PARAMETER RemoteRoot
Remote document root, default /var/www/agentdesk-update
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,

    [string]$SshTarget = "root@74.211.104.202",

    [string]$RemoteRoot = "/var/www/agentdesk-update",

    [string]$Repository = "rkshadow999/AgentDesk",

    [string]$PublicKeyPath,

    [string]$PrivateKeyPath,

    [string]$FeedBaseUrl = "https://update.rkshadow.com",

    [string]$OutputMetadataDirectory,

    [switch]$SkipUpload,

    [switch]$SkipInstaller
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

$scriptDirectory = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    $PSScriptRoot
}
else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}
$repositoryRoot = Get-FullPath (Join-Path $scriptDirectory "..\..")
if ([string]::IsNullOrWhiteSpace($PublicKeyPath)) {
    $PublicKeyPath = Join-Path $repositoryRoot "desktop\update\AgentDesk-update-public-key.spki.base64"
}
if ([string]::IsNullOrWhiteSpace($PrivateKeyPath)) {
    $PrivateKeyPath = Join-Path $env:USERPROFILE ".agentdesk\AgentDesk-update-private.pkcs8.der"
}

$releaseRoot = Get-FullPath $ReleaseDirectory
$portableZip = Join-Path $releaseRoot "AgentDesk-$Version-win-x64-portable.zip"
$updaterZip = Join-Path $releaseRoot "AgentDesk-$Version-win-x64-updater.zip"
foreach ($required in @($portableZip, $updaterZip)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Missing release asset: $required"
    }
}

$publicKeyFull = Get-FullPath $PublicKeyPath
$privateKeyFull = Get-FullPath $PrivateKeyPath
if (-not (Test-Path -LiteralPath $publicKeyFull)) {
    throw "Public key pin is missing: $publicKeyFull"
}
if (-not (Test-Path -LiteralPath $privateKeyFull)) {
    throw "Private signing key is missing: $privateKeyFull"
}

# Convert base64 SPKI pin to DER for the manifest signer.
$publicKeyText = [System.IO.File]::ReadAllText($publicKeyFull).Trim()
$publicKeyDer = [Convert]::FromBase64String($publicKeyText)
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("agentdesk-selfhost-" + [guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$tempPublicKey = Join-Path $tempRoot "AgentDesk-update-public-key.spki"
$tempPrivateKey = Join-Path $tempRoot "AgentDesk-update-private.pkcs8.der"
[System.IO.File]::WriteAllBytes($tempPublicKey, $publicKeyDer)
[System.IO.File]::Copy($privateKeyFull, $tempPrivateKey, $true)

if ([string]::IsNullOrWhiteSpace($OutputMetadataDirectory)) {
    $OutputMetadataDirectory = Join-Path $repositoryRoot "artifacts\selfhosted-feed\$Version"
}
$metadataRoot = Get-FullPath $OutputMetadataDirectory
if (Test-Path -LiteralPath $metadataRoot) {
    Remove-Item -LiteralPath $metadataRoot -Recurse -Force
}
[System.IO.Directory]::CreateDirectory($metadataRoot) | Out-Null

$assetBaseUrl = "$FeedBaseUrl/releases/v$Version"
$manifestScript = Join-Path $scriptDirectory "New-AgentDeskUpdateManifest.ps1"
Write-Host "Signing update manifests for $Version ..."
& $manifestScript `
    -Version $Version `
    -Repository $Repository `
    -Tag "v$Version" `
    -X64PackagePath $portableZip `
    -X64UpdaterPath $updaterZip `
    -PublicKeyPath $tempPublicKey `
    -PrivateKeyPath $tempPrivateKey `
    -OutputDirectory $metadataRoot `
    -AssetBaseUrl $assetBaseUrl

# Stage release assets next to metadata for upload.
Copy-Item -LiteralPath $portableZip -Destination (Join-Path $metadataRoot (Split-Path $portableZip -Leaf)) -Force
Copy-Item -LiteralPath $updaterZip -Destination (Join-Path $metadataRoot (Split-Path $updaterZip -Leaf)) -Force

$installerRoot = Join-Path $metadataRoot "installer"
[System.IO.Directory]::CreateDirectory($installerRoot) | Out-Null

if (-not $SkipInstaller) {
    $buildInstaller = Join-Path $scriptDirectory "Build-AgentDeskWindowsInstaller.ps1"
    if (Test-Path -LiteralPath $buildInstaller) {
        Write-Host "Building Windows installer package ..."
        & $buildInstaller `
            -Version $Version `
            -PortableZipPath $portableZip `
            -OutputDirectory $installerRoot
    }
    else {
        Write-Warning "Installer builder not found; packaging portable zip only for distribution."
        Copy-Item -LiteralPath $portableZip -Destination (Join-Path $installerRoot (Split-Path $portableZip -Leaf)) -Force
        $readmeLines = @(
            "AgentDesk $Version (Portable)",
            "",
            "1. Extract this zip to any folder (recommended: %LOCALAPPDATA%\AgentDesk).",
            "2. Run AgentDesk.App.exe.",
            "3. Enable update checks in Settings; the client pulls signed manifests from https://update.rkshadow.com/feed.",
            "",
            "Verify SHA-256 against the companion checksum file when available."
        )
        [System.IO.File]::WriteAllLines(
            (Join-Path $installerRoot "README-install.txt"),
            $readmeLines,
            [System.Text.UTF8Encoding]::new($false))
    }
}

if (-not $SkipUpload) {
    Write-Host "Uploading to $SshTarget ($RemoteRoot) ..."
    $remoteRelease = "$RemoteRoot/releases/v$Version"
    $remoteFeed = "$RemoteRoot/feed"
    $remoteInstall = "$RemoteRoot/install"

    ssh -o BatchMode=yes $SshTarget "mkdir -p '$remoteRelease' '$remoteFeed' '$remoteInstall'"
    if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed with exit code $LASTEXITCODE" }

    scp -o BatchMode=yes `
        (Join-Path $metadataRoot "AgentDesk-$Version-win-x64-portable.zip") `
        (Join-Path $metadataRoot "AgentDesk-$Version-win-x64-updater.zip") `
        "${SshTarget}:${remoteRelease}/"
    if ($LASTEXITCODE -ne 0) { throw "scp releases failed with exit code $LASTEXITCODE" }

    scp -o BatchMode=yes `
        (Join-Path $metadataRoot "AgentDesk-update-manifest.json") `
        (Join-Path $metadataRoot "AgentDesk-update-manifest.json.sig") `
        (Join-Path $metadataRoot "AgentDesk-updater-manifest.json") `
        (Join-Path $metadataRoot "AgentDesk-updater-manifest.json.sig") `
        (Join-Path $metadataRoot "AgentDesk-update-public-key.spki") `
        (Join-Path $metadataRoot "AgentDesk-update-metadata-SHA256SUMS.txt") `
        "${SshTarget}:${remoteFeed}/"
    if ($LASTEXITCODE -ne 0) { throw "scp feed failed with exit code $LASTEXITCODE" }

    # Publish latest installer / portable for humans.
    $installerFiles = Get-ChildItem -LiteralPath $installerRoot -File -ErrorAction SilentlyContinue
    if ($installerFiles) {
        foreach ($file in $installerFiles) {
            scp -o BatchMode=yes $file.FullName "${SshTarget}:${remoteInstall}/"
            if ($LASTEXITCODE -ne 0) { throw "scp install asset failed: $($file.Name)" }
        }
        scp -o BatchMode=yes `
            (Join-Path $metadataRoot "AgentDesk-$Version-win-x64-portable.zip") `
            "${SshTarget}:${remoteInstall}/AgentDesk-latest-win-x64-portable.zip"
    }

    $indexLines = @(
        '<!DOCTYPE html>',
        '<html lang="zh-CN">',
        '<head>',
        '  <meta charset="utf-8" />',
        '  <meta name="viewport" content="width=device-width, initial-scale=1" />',
        '  <title>AgentDesk Download</title>',
        '  <style>',
        '    body { font-family: system-ui, sans-serif; max-width: 44rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; color: #e8e8e8; background: #181A19; }',
        '    a { color: #7dd3a7; }',
        '    code { background: #2a2d2c; padding: 0.1rem 0.35rem; border-radius: 4px; }',
        '    .card { border: 1px solid #333; border-radius: 12px; padding: 1.25rem; margin: 1rem 0; background: #1f2221; }',
        '  </style>',
        '</head>',
        '<body>',
        "  <h1>AgentDesk $Version</h1>",
        '  <p>Windows x64 installer / Portable distribution and signed update channel.</p>',
        '  <div class="card">',
        '    <h2>Download</h2>',
        '    <ul>',
        '      <li><a href="AgentDesk-latest-win-x64-portable.zip">Portable zip</a></li>',
        "      <li><a href=`"AgentDesk-$Version-win-x64-Setup.exe`">Setup.exe</a></li>",
        '    </ul>',
        '  </div>',
        '  <div class="card">',
        '    <h2>Auto-update</h2>',
        '    <p>Feed: <code>https://update.rkshadow.com/feed/</code></p>',
        "    <p>Assets: <code>https://update.rkshadow.com/releases/v$Version/</code></p>",
        '    <p>Manifests are ECDSA P-256 detached signatures; public key is pinned in the client.</p>',
        '  </div>',
        '</body>',
        '</html>'
    )
    $indexPath = Join-Path $tempRoot "index.html"
    [System.IO.File]::WriteAllLines($indexPath, $indexLines, [System.Text.UTF8Encoding]::new($false))
    scp -o BatchMode=yes $indexPath "${SshTarget}:${remoteInstall}/index.html"

    ssh -o BatchMode=yes $SshTarget "chmod -R a+rX '$RemoteRoot'; find '$RemoteRoot' -type d -exec chmod 755 {} \;; find '$RemoteRoot' -type f -exec chmod 644 {} \;"
    if ($LASTEXITCODE -ne 0) { throw "ssh chmod failed with exit code $LASTEXITCODE" }

    Write-Host "Verifying public feed ..."
    $verifyUrls = @(
        "$FeedBaseUrl/feed/AgentDesk-update-manifest.json",
        "$FeedBaseUrl/feed/AgentDesk-update-manifest.json.sig",
        "$FeedBaseUrl/releases/v$Version/AgentDesk-$Version-win-x64-portable.zip"
    )
    foreach ($url in $verifyUrls) {
        $response = curl.exe -sS -o NUL -w "%{http_code}" --max-time 120 $url
        if ($response -ne "200") {
            throw "Public URL not ready ($response): $url"
        }
        Write-Host "  OK $url"
    }
}

Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Self-hosted update publish complete."
Write-Host "  Metadata: $metadataRoot"
Write-Host "  Feed:     $FeedBaseUrl/feed/"
Write-Host "  Assets:   $FeedBaseUrl/releases/v$Version/"
Write-Host "  Install:  $FeedBaseUrl/install/"
