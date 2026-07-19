<#
.SYNOPSIS
Builds AgentDesk portable and/or MSIX package inputs for one Windows architecture.

.DESCRIPTION
Publishes the WinUI 3 application, places the native sidecar at the application
default path, optionally bundles the architecture-matched Linux WSL sidecar, and injects the same
engines and legal notices into an unsigned or PFX-signed MSIX.

.PARAMETER Architecture
Windows architecture to publish: x64 or arm64.

.PARAMETER Mode
Package modes to build: Portable, MSIX, or All.

.PARAMETER NativeEnginePath
Path to the architecture-matched xai-grok-pager executable.

.PARAMETER WslEnginePath
Optional path to the architecture-matched Linux sidecar used by WslStrict.

.PARAMETER CertificatePath
Optional PFX path. Signing is enabled only when both this and CertificatePassword are provided.

.PARAMETER SignToolPath
Optional explicit path to signtool.exe used to verify a signed MSIX.

.PARAMETER ExpectedSignerThumbprint
Optional repository-pinned SHA-1 thumbprint for the AgentDesk signing certificate.

.PARAMETER UpdaterProject
Optional explicit path to the AgentDesk updater console project.

.PARAMETER DryRun
Prints the resolved commands and output paths without changing files.
#>
[CmdletBinding()]
param(
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",

    [ValidateSet("Portable", "MSIX", "All")]
    [string]$Mode = "All",

    [string]$Configuration = "Release",
    [string]$Version = "0.1.0-ci.0",
    [string]$AppProject,
    [string]$UpdaterProject,
    [string]$NativeEnginePath,
    [string]$WslEnginePath,
    [string]$OutputRoot,
    [string]$SourceRepository = $env:GITHUB_REPOSITORY,
    [string]$SourceRevision = $env:GITHUB_SHA,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [string]$SignToolPath,
    [string]$ExpectedSignerThumbprint,
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
        throw "Refusing to reset a directory outside the package root: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    [System.IO.Directory]::CreateDirectory($fullPath) | Out-Null
    return $fullPath
}

function Format-CommandArgument {
    param([string]$Value)
    if ($Value -match '[\s"]') {
        return '"' + $Value.Replace('"', '\"') + '"'
    }
    return $Value
}

function Invoke-ExternalCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments
    )
    $displayArguments = $Arguments | ForEach-Object { Format-CommandArgument $_ }
    Write-Host "> $FilePath $($displayArguments -join ' ')"
    if ($DryRun) {
        return
    }
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required package input does not exist: $Source"
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Assert-NativeEngineRevision {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedRevision
    )

    $normalizedRevision = $ExpectedRevision.Trim()
    if ($normalizedRevision -notmatch '^[0-9A-Fa-f]{7,40}$') {
        throw "SourceRevision must be a hexadecimal Git revision with at least 7 characters: $ExpectedRevision"
    }

    $timeoutMilliseconds = 5000
    $maximumOutputCharacters = 16 * 1024
    $process = [System.Diagnostics.Process]::new()
    $processStarted = $false
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Path
    $startInfo.Arguments = "--version"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process.StartInfo = $startInfo
    $stdout = [System.Text.StringBuilder]::new()
    $stderr = [System.Text.StringBuilder]::new()
    $stdoutBuffer = [char[]]::new(1024)
    $stderrBuffer = [char[]]::new(1024)
    $stdoutComplete = $false
    $stderrComplete = $false
    $stdoutRead = $null
    $stderrRead = $null
    try {
        $processStarted = $process.Start()
        if (-not $processStarted) {
            throw "Native sidecar --version could not be started: $Path"
        }
        $stdoutRead = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderrRead = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        $deadline = [DateTimeOffset]::UtcNow.AddMilliseconds($timeoutMilliseconds)
        while (-not ($stdoutComplete -and $stderrComplete -and $process.HasExited)) {
            if (-not $stdoutComplete -and $stdoutRead.IsCompleted) {
                $charactersRead = $stdoutRead.GetAwaiter().GetResult()
                if ($charactersRead -eq 0) {
                    $stdoutComplete = $true
                }
                else {
                    if ($stdout.Length + $stderr.Length + $charactersRead -gt $maximumOutputCharacters) {
                        throw "Native sidecar --version output exceeded $maximumOutputCharacters characters: $Path"
                    }
                    [void]$stdout.Append($stdoutBuffer, 0, $charactersRead)
                    $stdoutRead = $process.StandardOutput.ReadAsync(
                        $stdoutBuffer,
                        0,
                        $stdoutBuffer.Length)
                }
            }
            if (-not $stderrComplete -and $stderrRead.IsCompleted) {
                $charactersRead = $stderrRead.GetAwaiter().GetResult()
                if ($charactersRead -eq 0) {
                    $stderrComplete = $true
                }
                else {
                    if ($stdout.Length + $stderr.Length + $charactersRead -gt $maximumOutputCharacters) {
                        throw "Native sidecar --version output exceeded $maximumOutputCharacters characters: $Path"
                    }
                    [void]$stderr.Append($stderrBuffer, 0, $charactersRead)
                    $stderrRead = $process.StandardError.ReadAsync(
                        $stderrBuffer,
                        0,
                        $stderrBuffer.Length)
                }
            }
            if ([DateTimeOffset]::UtcNow -ge $deadline) {
                throw "Native sidecar --version timed out after $timeoutMilliseconds milliseconds: $Path"
            }
            $process.Refresh()
            if (-not ($stdoutComplete -and $stderrComplete -and $process.HasExited)) {
                Start-Sleep -Milliseconds 10
            }
        }
        $process.WaitForExit()
        $process.Refresh()
        if ($process.ExitCode -ne 0) {
            throw "Native sidecar --version exited with code $($process.ExitCode): $Path"
        }

        $versionOutput = $stdout.ToString() + "`n" + $stderr.ToString()
        $revisionTokens = [System.Text.RegularExpressions.Regex]::Matches(
            $versionOutput,
            '(?i)(?<![0-9a-f])[0-9a-f]{7,40}(?![0-9a-f])')
        $revisionMatches = $false
        foreach ($tokenMatch in $revisionTokens) {
            $token = $tokenMatch.Value
            $comparisonLength = [Math]::Min(
                12,
                [Math]::Min($normalizedRevision.Length, $token.Length))
            if ($comparisonLength -ge 7 -and [string]::Equals(
                    $normalizedRevision.Substring(0, $comparisonLength),
                    $token.Substring(0, $comparisonLength),
                    [System.StringComparison]::OrdinalIgnoreCase)) {
                $revisionMatches = $true
                break
            }
        }
        if (-not $revisionMatches) {
            $expectedPrefixLength = [Math]::Min(12, $normalizedRevision.Length)
            $expectedPrefix = $normalizedRevision.Substring(0, $expectedPrefixLength)
            throw "Native sidecar revision mismatch; expected revision prefix $expectedPrefix in --version output: $Path"
        }
    }
    finally {
        if ($processStarted) {
            if (-not $process.HasExited) {
                try {
                    $process.Kill()
                    $process.WaitForExit()
                }
                catch {
                }
            }
        }
        $process.Dispose()
    }
}

function ConvertTo-WindowsVersions {
    param([Parameter(Mandatory)][string]$InputVersion)

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $InputVersion,
        '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(ci|alpha|beta|preview|rc)\.(0|[1-9]\d*))?$')
    if (-not $match.Success) {
        throw "Version must omit a leading v and be stable SemVer or a supported numbered prerelease (ci, alpha, beta, preview, rc), for example 0.2.0 or 0.2.0-rc.1: $InputVersion"
    }

    $components = foreach ($groupIndex in 1..3) {
        [uint64]$component = 0
        if (-not [uint64]::TryParse($match.Groups[$groupIndex].Value, [ref]$component) -or
            $component -gt 65535) {
            throw "MSIX version components must be between 0 and 65535: $InputVersion"
        }
        [int]$component
    }

    $revision = 65535
    if ($match.Groups[4].Success) {
        [uint64]$sequence = 0
        if (-not [uint64]::TryParse($match.Groups[5].Value, [ref]$sequence) -or
            $sequence -gt 9999) {
            throw "Prerelease sequence must be between 0 and 9999: $InputVersion"
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

    return [pscustomobject]@{
        Msix = "$($components[0]).$($components[1]).$($components[2]).$revision"
        Assembly = "$($components[0]).$($components[1]).$($components[2]).0"
    }
}

$repositoryRoot = Get-FullPath (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($AppProject)) {
    $AppProject = Join-Path $repositoryRoot "desktop\src\AgentDesk.App\AgentDesk.App.csproj"
}
if ([string]::IsNullOrWhiteSpace($UpdaterProject)) {
    $UpdaterProject = Join-Path $repositoryRoot "desktop\src\AgentDesk.Updater\AgentDesk.Updater.csproj"
}
if ([string]::IsNullOrWhiteSpace($NativeEnginePath)) {
    $NativeEnginePath = Join-Path $repositoryRoot "target\release-dist\xai-grok-pager.exe"
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot "artifacts\agentdesk"
}
if ([string]::IsNullOrWhiteSpace($SourceRepository)) {
    $SourceRepository = "local-worktree"
}
if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
    $SourceRevision = "uncommitted"
}

$AppProject = Get-FullPath $AppProject
$UpdaterProject = Get-FullPath $UpdaterProject
$NativeEnginePath = Get-FullPath $NativeEnginePath
$OutputRoot = Get-FullPath $OutputRoot
if (-not [string]::IsNullOrWhiteSpace($WslEnginePath)) {
    $WslEnginePath = Get-FullPath $WslEnginePath
}
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    $CertificatePath = Get-FullPath $CertificatePath
}
if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
    $SignToolPath = Get-FullPath $SignToolPath
}

$platform = if ($Architecture -eq "arm64") { "ARM64" } else { "x64" }
$runtimeIdentifier = "win-$Architecture"
$safeVersion = ($Version -replace '[^0-9A-Za-z._-]', '-')
$windowsVersions = ConvertTo-WindowsVersions $Version
$inputRoot = Join-Path $OutputRoot "package-input-$Architecture"
$portableDirectory = Join-Path $inputRoot "portable"
$msixDirectory = Join-Path $inputRoot "msix"
$metadataDirectory = Join-Path $inputRoot "metadata"
$updaterPublishDirectory = Join-Path $metadataDirectory "updater-publish"
$updateStagingDirectory = Join-Path $inputRoot "update-staging"
$licensePath = Join-Path $repositoryRoot "LICENSE"
$thirdPartyNoticesPath = Join-Path $repositoryRoot "THIRD-PARTY-NOTICES"
$desktopNoticesPath = Join-Path $repositoryRoot "desktop\THIRD-PARTY-NOTICES.md"
$sourceNoticePath = Join-Path $repositoryRoot "desktop\THIRD-PARTY-SOURCE-NOTICE.md"
$sourceNoticeZhCnPath = Join-Path $repositoryRoot "desktop\THIRD-PARTY-SOURCE-NOTICE.zh-CN.md"
$wslInstallerPath = Join-Path $PSScriptRoot "Install-AgentDeskWslEngine.ps1"
$packagingTargetsPath = Join-Path $PSScriptRoot "AgentDesk.Packaging.targets"
$signatureVerifierPath = Join-Path $PSScriptRoot "Verify-AgentDeskMsixSignature.ps1"
$engineArchitectureVerifierPath = Join-Path $PSScriptRoot "Test-AgentDeskEngineArchitecture.ps1"
$linuxEngineArchitectureVerifierPath = Join-Path $PSScriptRoot "Test-AgentDeskLinuxEngineArchitecture.ps1"
$sourcePackageManifestPath = Join-Path $repositoryRoot "desktop\src\AgentDesk.App\Package.appxmanifest"

if (($CertificatePath -and -not $CertificatePassword) -or
    ($CertificatePassword -and -not $CertificatePath)) {
    throw "CertificatePath and CertificatePassword must be provided together."
}

Write-Host "AgentDesk package input: $inputRoot"
Write-Host "Updater staging: $updateStagingDirectory"
if ($DryRun) {
    Write-Host "Dry run enabled; no files will be changed."
}
else {
    if (-not (Test-Path -LiteralPath $AppProject -PathType Leaf)) {
        throw "AgentDesk App project was not found: $AppProject"
    }
    if (-not (Test-Path -LiteralPath $UpdaterProject -PathType Leaf)) {
        throw "AgentDesk Updater project was not found: $UpdaterProject"
    }
    if (-not (Test-Path -LiteralPath $NativeEnginePath -PathType Leaf)) {
        throw "Native sidecar was not found: $NativeEnginePath"
    }
    & $engineArchitectureVerifierPath -Path $NativeEnginePath -Architecture $Architecture
    Assert-NativeEngineRevision -Path $NativeEnginePath -ExpectedRevision $SourceRevision
    if ($WslEnginePath -and -not (Test-Path -LiteralPath $WslEnginePath -PathType Leaf)) {
        throw "WSL sidecar was not found: $WslEnginePath"
    }
    if ($WslEnginePath) {
        & $linuxEngineArchitectureVerifierPath -Path $WslEnginePath -Architecture $Architecture
    }
    if ($CertificatePath -and -not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "PFX certificate was not found: $CertificatePath"
    }
    [System.IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
    $inputRoot = Reset-ChildDirectory -Path $inputRoot -AllowedRoot $OutputRoot
    [System.IO.Directory]::CreateDirectory($metadataDirectory) | Out-Null
}

$sourceRevisionPath = Join-Path $metadataDirectory "SOURCE-REVISION.txt"
$generatedPackageManifestPath = Join-Path $metadataDirectory "Package.generated.appxmanifest"
if (-not $DryRun) {
    $sourceText = @(
        "AgentDesk source distribution",
        "Repository: $SourceRepository",
        "Revision: $SourceRevision",
        "Build version: $Version",
        "MSIX identity version: $($windowsVersions.Msix)",
        "Generated (UTC): $([DateTimeOffset]::UtcNow.ToString('O'))",
        "See THIRD-PARTY-SOURCE-NOTICE.md for MPL-2.0 source availability."
    ) -join "`n"
    [System.IO.File]::WriteAllText(
        $sourceRevisionPath,
        $sourceText + "`n",
        [System.Text.UTF8Encoding]::new($false))
    [xml]$packageManifest = Get-Content -LiteralPath $sourcePackageManifestPath -Raw
    $packageManifest.Package.Identity.SetAttribute("Version", $windowsVersions.Msix)
    $packageManifest.Save($generatedPackageManifestPath)
}

if (-not $DryRun) {
    $updaterPublishDirectory = Reset-ChildDirectory `
        -Path $updaterPublishDirectory `
        -AllowedRoot $inputRoot
    $updateStagingDirectory = Reset-ChildDirectory `
        -Path $updateStagingDirectory `
        -AllowedRoot $inputRoot
}
$updaterArguments = @(
    "publish",
    $UpdaterProject,
    "--configuration", $Configuration,
    "--runtime", $runtimeIdentifier,
    "--self-contained", "true",
    "--output", $updaterPublishDirectory,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-p:Version=$Version",
    "-p:InformationalVersion=$Version",
    "-p:AssemblyVersion=$($windowsVersions.Assembly)",
    "-p:FileVersion=$($windowsVersions.Msix)"
)
Invoke-ExternalCommand -FilePath "dotnet" -Arguments $updaterArguments

if (-not $DryRun) {
    $publishedUpdater = Join-Path $updaterPublishDirectory "AgentDesk.Updater.exe"
    Copy-RequiredFile `
        -Source $publishedUpdater `
        -Destination (Join-Path $updateStagingDirectory "AgentDesk.Updater.exe")
    [System.IO.File]::WriteAllText(
        (Join-Path $updateStagingDirectory "DEVELOPMENT-ONLY.txt"),
        "This updater package input is development-only until a trusted release manifest and detached signature are generated.`n",
        [System.Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $updaterPublishDirectory -Recurse -Force
}

if ($Mode -in @("Portable", "All")) {
    if (-not $DryRun) {
        $portableDirectory = Reset-ChildDirectory -Path $portableDirectory -AllowedRoot $inputRoot
    }
    $portableArguments = @(
        "publish",
        $AppProject,
        "--configuration", $Configuration,
        "--runtime", $runtimeIdentifier,
        "--self-contained", "true",
        "--output", $portableDirectory,
        "-p:Platform=$platform",
        "-p:PackageMode=Portable",
        "-p:WindowsPackageType=None",
        "-p:PublishSingleFile=false",
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-p:AssemblyVersion=$($windowsVersions.Assembly)",
        "-p:FileVersion=$($windowsVersions.Msix)"
    )
    Invoke-ExternalCommand -FilePath "dotnet" -Arguments $portableArguments

    if (-not $DryRun) {
        Copy-RequiredFile -Source $NativeEnginePath -Destination (Join-Path $portableDirectory "agentdesk-engine.exe")
        if ($WslEnginePath) {
            $wslDirectory = Join-Path $portableDirectory "wsl"
            [System.IO.Directory]::CreateDirectory($wslDirectory) | Out-Null
            Copy-RequiredFile -Source $WslEnginePath -Destination (Join-Path $wslDirectory "agentdesk-engine")
            Copy-RequiredFile -Source $wslInstallerPath -Destination (Join-Path $portableDirectory "Install-AgentDeskWslEngine.ps1")
        }
        Copy-RequiredFile -Source $licensePath -Destination (Join-Path $portableDirectory "LICENSE")
        Copy-RequiredFile -Source $thirdPartyNoticesPath -Destination (Join-Path $portableDirectory "THIRD-PARTY-NOTICES")
        Copy-RequiredFile -Source $desktopNoticesPath -Destination (Join-Path $portableDirectory "THIRD-PARTY-NOTICES.md")
        Copy-RequiredFile -Source $sourceNoticePath -Destination (Join-Path $portableDirectory "THIRD-PARTY-SOURCE-NOTICE.md")
        Copy-RequiredFile -Source $sourceNoticeZhCnPath -Destination (Join-Path $portableDirectory "THIRD-PARTY-SOURCE-NOTICE.zh-CN.md")
        Copy-RequiredFile -Source $sourceRevisionPath -Destination (Join-Path $portableDirectory "SOURCE-REVISION.txt")
    }
}

if ($Mode -in @("MSIX", "All")) {
    if (-not $DryRun) {
        $msixDirectory = Reset-ChildDirectory -Path $msixDirectory -AllowedRoot $inputRoot
    }
    $msixPackageDirectory = $msixDirectory.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $msixArguments = @(
        "publish",
        $AppProject,
        "--configuration", $Configuration,
        "--runtime", $runtimeIdentifier,
        "--self-contained", "true",
        "-p:Platform=$platform",
        "-p:PackageMode=MSIX",
        "-p:WindowsPackageType=MSIX",
        "-p:GenerateAppxPackageOnBuild=true",
        "-p:AppxBundle=Never",
        "-p:UapAppxPackageBuildMode=SideloadOnly",
        "-p:AppxPackageDir=$msixPackageDirectory",
        "-p:AppxSymbolPackageEnabled=false",
        "-p:AppxPackageIncludePrivateSymbols=false",
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-p:AssemblyVersion=$($windowsVersions.Assembly)",
        "-p:FileVersion=$($windowsVersions.Msix)",
        "-p:CustomAfterMicrosoftCommonTargets=$packagingTargetsPath",
        "-p:AgentDeskPackageManifestPath=$generatedPackageManifestPath",
        "-p:AgentDeskNativeEnginePath=$NativeEnginePath",
        "-p:AgentDeskWslEnginePath=$WslEnginePath",
        "-p:AgentDeskWslInstallerPath=$wslInstallerPath",
        "-p:AgentDeskLicensePath=$licensePath",
        "-p:AgentDeskThirdPartyNoticesPath=$thirdPartyNoticesPath",
        "-p:AgentDeskDesktopNoticesPath=$desktopNoticesPath",
        "-p:AgentDeskSourceNoticePath=$sourceNoticePath",
        "-p:AgentDeskSourceNoticeZhCnPath=$sourceNoticeZhCnPath",
        "-p:AgentDeskSourceRevisionPath=$sourceRevisionPath"
    )

    $previousCertificatePassword = $env:PackageCertificatePassword
    try {
        if ($CertificatePath) {
            $env:PackageCertificatePassword = $CertificatePassword
            $msixArguments += @(
                "-p:AppxPackageSigningEnabled=true",
                "-p:PackageCertificateKeyFile=$CertificatePath"
            )
        }
        else {
            $msixArguments += "-p:AppxPackageSigningEnabled=false"
        }
        Invoke-ExternalCommand -FilePath "dotnet" -Arguments $msixArguments
    }
    finally {
        if ($null -eq $previousCertificatePassword) {
            Remove-Item Env:PackageCertificatePassword -ErrorAction SilentlyContinue
        }
        else {
            $env:PackageCertificatePassword = $previousCertificatePassword
        }
    }

    if (-not $DryRun) {
        $builtMsix = Get-ChildItem -LiteralPath $msixDirectory -Recurse -File -Filter "*.msix" |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if (-not $builtMsix) {
            throw "MSIX packaging completed without producing a .msix file under $msixDirectory"
        }
        $stableMsixPath = Join-Path $msixDirectory "AgentDesk-$safeVersion-win-$Architecture.msix"
        if ($builtMsix.FullName -ne $stableMsixPath) {
            Copy-Item -LiteralPath $builtMsix.FullName -Destination $stableMsixPath -Force
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $msixArchive = [System.IO.Compression.ZipFile]::OpenRead($stableMsixPath)
        try {
            $hasSignature = $null -ne ($msixArchive.Entries |
                Where-Object FullName -eq "AppxSignature.p7x" |
                Select-Object -First 1)
            $manifestEntry = $msixArchive.GetEntry("AppxManifest.xml")
            if (-not $manifestEntry) {
                throw "Packaged MSIX is missing AppxManifest.xml."
            }
            $manifestReader = [System.IO.StreamReader]::new($manifestEntry.Open())
            try {
                $packagedManifest = $manifestReader.ReadToEnd()
            }
            finally {
                $manifestReader.Dispose()
            }
        }
        finally {
            $msixArchive.Dispose()
        }
        if ($CertificatePath -and -not $hasSignature) {
            throw "MSIX signing was requested, but AppxSignature.p7x is missing."
        }
        if (-not $CertificatePath -and $hasSignature) {
            throw "Unsigned MSIX packaging unexpectedly produced a signature."
        }
        if (-not $packagedManifest.Contains("Version=`"$($windowsVersions.Msix)`"")) {
            throw "MSIX identity version does not match the requested release version $($windowsVersions.Msix)."
        }
        if ($CertificatePath) {
            $verificationParameters = @{
                PackagePath = $stableMsixPath
                ExpectedPackageName = "AgentDesk"
                ExpectedPackageVersion = $windowsVersions.Msix
                ExpectedArchitecture = $Architecture
            }
            if ($SignToolPath) {
                $verificationParameters.SignToolPath = $SignToolPath
            }
            if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
                $verificationParameters.ExpectedThumbprint = $ExpectedSignerThumbprint
            }
            & $signatureVerifierPath @verificationParameters
        }
        Get-ChildItem -LiteralPath $msixDirectory -Force |
            Where-Object {
                -not [System.String]::Equals(
                    $_.FullName,
                    $stableMsixPath,
                    [System.StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force
            }
        $signingStatus = if ($CertificatePath) { "signed" } else { "unsigned" }
        [System.IO.File]::WriteAllText(
            (Join-Path $msixDirectory "MSIX-SIGNING-STATUS.txt"),
            "$signingStatus`n",
            [System.Text.UTF8Encoding]::new($false))
    }
}

Write-Output $inputRoot
