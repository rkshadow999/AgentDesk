<#
.SYNOPSIS
Runs focused regression tests for AgentDesk release scripts.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$buildScript = Join-Path $PSScriptRoot "Build-AgentDeskPackage.ps1"
$noticeGenerator = Join-Path $PSScriptRoot "Generate-AgentDeskThirdPartyNotices.ps1"
$engineArchitectureVerifier = Join-Path $PSScriptRoot "Test-AgentDeskEngineArchitecture.ps1"
$engineRevisionTest = Join-Path $PSScriptRoot "Test-AgentDeskEngineRevision.ps1"
$linuxEngineArchitectureVerifier = Join-Path $PSScriptRoot "Test-AgentDeskLinuxEngineArchitecture.ps1"
$rollbackTest = Join-Path $PSScriptRoot "Test-AgentDeskRollbackBundle.ps1"
$updateManifestTest = Join-Path $PSScriptRoot "Test-AgentDeskUpdateManifest.ps1"
$updateFeedAdvanceTest = Join-Path $PSScriptRoot "Test-AgentDeskUpdateFeedAdvance.ps1"
$wslInstallerTest = Join-Path $PSScriptRoot "Test-AgentDeskWslInstaller.ps1"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$rustVersionBuildScripts = @(
    Join-Path $repositoryRoot "crates\codegen\xai-grok-pager\build.rs"
    Join-Path $repositoryRoot "crates\codegen\xai-grok-pager-bin\build.rs"
)

& $rollbackTest
& $updateFeedAdvanceTest
& $wslInstallerTest
& $engineRevisionTest

foreach ($rustVersionBuildScript in $rustVersionBuildScripts) {
    $source = Get-Content -LiteralPath $rustVersionBuildScript -Raw
    if ($source.Contains('cargo:rerun-if-changed=.git/HEAD')) {
        throw "Rust version metadata must not watch a crate-local .git/HEAD path."
    }
    foreach ($requiredGitWatch in @(
        'git_metadata_path("HEAD")',
        'git_metadata_path("logs/HEAD")'
    )) {
        if (-not $source.Contains($requiredGitWatch)) {
            throw "Rust version metadata is missing worktree-aware watch '$requiredGitWatch'."
        }
    }
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Get-DryRunOutput {
    param(
        [Parameter(Mandatory)][string]$Version,
        [ValidateSet("Portable", "MSIX", "All")]
        [string]$Mode = "MSIX"
    )

    return (& $buildScript `
        -Architecture x64 `
        -Mode $Mode `
        -Version $Version `
        -NativeEnginePath (Join-Path $PSScriptRoot "missing-native-engine.exe") `
        -OutputRoot (Join-Path $PSScriptRoot "test-output") `
        -DryRun *>&1 | Out-String -Width 4096)
}

function Get-MsixVersion {
    param([Parameter(Mandatory)][string]$Version)

    $output = Get-DryRunOutput -Version $Version
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $output,
        '-p:FileVersion=(\d+\.\d+\.\d+\.\d+)')
    if (-not $match.Success) {
        throw "Dry run did not report an MSIX file version for $Version.`n$output"
    }
    return [version]$match.Groups[1].Value
}

function Assert-ThrowsVersion {
    param(
        [Parameter(Mandatory)][string]$Version,
        [ValidateSet("Portable", "MSIX", "All")]
        [string]$Mode = "MSIX"
    )

    try {
        Get-DryRunOutput -Version $Version -Mode $Mode | Out-Null
    }
    catch {
        return
    }
    throw "Expected version '$Version' to be rejected."
}

function Write-MinimalPeFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][uint16]$Machine,
        [Parameter(Mandatory)][uint64]$StackReserveBytes
    )

    $bytes = [byte[]]::new(512)
    $bytes[0] = 0x4d
    $bytes[1] = 0x5a
    [BitConverter]::GetBytes([int]0x80).CopyTo($bytes, 0x3c)
    $bytes[0x80] = 0x50
    $bytes[0x81] = 0x45
    [BitConverter]::GetBytes($Machine).CopyTo($bytes, 0x84)
    [BitConverter]::GetBytes([uint16]0x00f0).CopyTo($bytes, 0x94)
    [BitConverter]::GetBytes([uint16]0x020b).CopyTo($bytes, 0x98)
    [BitConverter]::GetBytes($StackReserveBytes).CopyTo($bytes, 0xe0)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Write-MinimalElfFixture {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][uint16]$Machine
    )

    $bytes = [byte[]]::new(20)
    $bytes[0] = 0x7f
    $bytes[1] = 0x45
    $bytes[2] = 0x4c
    $bytes[3] = 0x46
    $bytes[4] = 2
    $bytes[5] = 1
    $bytes[6] = 1
    [BitConverter]::GetBytes([uint16]3).CopyTo($bytes, 16)
    [BitConverter]::GetBytes($Machine).CopyTo($bytes, 18)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

$mappedVersions = [ordered]@{
    "0.2.0-ci.7" = [version]"0.2.0.7"
    "0.2.0-alpha.7" = [version]"0.2.0.10007"
    "0.2.0-beta.7" = [version]"0.2.0.20007"
    "0.2.0-preview.7" = [version]"0.2.0.30007"
    "0.2.0-rc.7" = [version]"0.2.0.40007"
    "0.2.0" = [version]"0.2.0.65535"
}

$previous = $null
foreach ($entry in $mappedVersions.GetEnumerator()) {
    $actual = Get-MsixVersion -Version $entry.Key
    if ($actual -ne $entry.Value) {
        throw "Expected $($entry.Key) to map to $($entry.Value), but got $actual."
    }
    if ($null -ne $previous -and $actual -le $previous) {
        throw "Expected $($entry.Key) to map above the preceding release channel."
    }
    $previous = $actual
}

foreach ($unsupportedVersion in @(
    "v0.2.0-alpha.1",
    "0.2.0-dev.1",
    "0.2.0-nightly.1",
    "0.2.0-rc1",
    "0.2.0-rc.01",
    "0.2.0-rc.10000",
    "0.2.0+build.1",
    "0.2.0.1"
)) {
    Assert-ThrowsVersion -Version $unsupportedVersion -Mode All
}

$updaterDryRun = Get-DryRunOutput -Version "0.2.0-ci.7"
foreach ($requiredUpdaterPublishSetting in @(
    'desktop\src\AgentDesk.Updater\AgentDesk.Updater.csproj',
    '--runtime win-x64',
    '--self-contained true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    'package-input-x64\update-staging'
)) {
    if (-not $updaterDryRun.Contains($requiredUpdaterPublishSetting)) {
        throw "Updater dry run is missing '$requiredUpdaterPublishSetting'.`n$updaterDryRun"
    }
}
if ($updaterDryRun.Contains('portable\update-staging')) {
    throw "The updater staging directory must remain outside the replaceable portable application payload."
}

$metadataVersion = "0.2.0-alpha.1"
$metadataModeCounts = [ordered]@{
    Portable = 2
    MSIX = 2
    All = 3
}
foreach ($mode in $metadataModeCounts.Keys) {
    $metadataDryRun = Get-DryRunOutput -Version $metadataVersion -Mode $mode
    $informationalVersionCount = [System.Text.RegularExpressions.Regex]::Matches(
        $metadataDryRun,
        [System.Text.RegularExpressions.Regex]::Escape(
            "-p:InformationalVersion=$metadataVersion")).Count
    if ($informationalVersionCount -ne $metadataModeCounts[$mode]) {
        throw "The $mode package dry run must stamp the complete SemVer on every updater and app assembly invocation."
    }
}

$engineFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-engine-architecture-test-" + [guid]::NewGuid().ToString("N"))
try {
    [System.IO.Directory]::CreateDirectory($engineFixtureRoot) | Out-Null
    $x64Engine = Join-Path $engineFixtureRoot "engine-x64.exe"
    $arm64Engine = Join-Path $engineFixtureRoot "engine-arm64.exe"
    $smallStackEngine = Join-Path $engineFixtureRoot "engine-small-stack.exe"
    $x64LinuxEngine = Join-Path $engineFixtureRoot "engine-linux-x64"
    $arm64LinuxEngine = Join-Path $engineFixtureRoot "engine-linux-arm64"
    $minimumStackReserveBytes = [uint64](8 * 1024 * 1024)
    Write-MinimalPeFixture `
        -Path $x64Engine `
        -Machine 0x8664 `
        -StackReserveBytes $minimumStackReserveBytes
    Write-MinimalPeFixture `
        -Path $arm64Engine `
        -Machine 0xaa64 `
        -StackReserveBytes $minimumStackReserveBytes
    Write-MinimalPeFixture `
        -Path $smallStackEngine `
        -Machine 0x8664 `
        -StackReserveBytes (1 * 1024 * 1024)
    Write-MinimalElfFixture -Path $x64LinuxEngine -Machine 0x003e
    Write-MinimalElfFixture -Path $arm64LinuxEngine -Machine 0x00b7

    & $engineArchitectureVerifier -Path $x64Engine -Architecture x64
    & $engineArchitectureVerifier -Path $arm64Engine -Architecture arm64
    try {
        & $engineArchitectureVerifier -Path $smallStackEngine -Architecture x64
    }
    catch {
        if ($_.Exception.Message -notmatch "stack reserve") {
            throw
        }
        $smallStackEngine = $null
    }
    if ($null -ne $smallStackEngine) {
        throw "Expected a PE sidecar with a 1 MiB stack reserve to be rejected."
    }
    foreach ($mismatch in @(
        @{ Path = $x64Engine; Architecture = "arm64" },
        @{ Path = $arm64Engine; Architecture = "x64" }
    )) {
        try {
            & $engineArchitectureVerifier @mismatch
        }
        catch {
            if ($_.Exception.Message -notmatch "architecture") {
                throw
            }
            continue
        }
        throw "Expected $($mismatch.Path) to be rejected for $($mismatch.Architecture)."
    }

    & $linuxEngineArchitectureVerifier -Path $x64LinuxEngine -Architecture x64
    & $linuxEngineArchitectureVerifier -Path $arm64LinuxEngine -Architecture arm64
    foreach ($mismatch in @(
        @{ Path = $x64LinuxEngine; Architecture = "arm64" },
        @{ Path = $arm64LinuxEngine; Architecture = "x64" }
    )) {
        try {
            & $linuxEngineArchitectureVerifier @mismatch
        }
        catch {
            if ($_.Exception.Message -notmatch "architecture") {
                throw
            }
            continue
        }
        throw "Expected $($mismatch.Path) to be rejected for $($mismatch.Architecture)."
    }
}
finally {
    Remove-Item -LiteralPath $engineFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$solutionPath = Join-Path $repositoryRoot "desktop\AgentDesk.sln"
$solutionProjects = (& dotnet sln $solutionPath list 2>&1 | Out-String) -replace '\\', '/'
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect AgentDesk.sln.`n$solutionProjects"
}
foreach ($requiredUpdaterProject in @(
    'src/AgentDesk.Updater.Core/AgentDesk.Updater.Core.csproj',
    'src/AgentDesk.Updater/AgentDesk.Updater.csproj',
    'tests/AgentDesk.Updater.Core.Tests/AgentDesk.Updater.Core.Tests.csproj'
)) {
    if (-not $solutionProjects.Contains($requiredUpdaterProject)) {
        throw "AgentDesk.sln is missing updater project '$requiredUpdaterProject'."
    }
}

$pinnedUpdateKeyPath = Join-Path $repositoryRoot "desktop\update\AgentDesk-update-public-key.spki.base64"
if (-not (Test-Path -LiteralPath $pinnedUpdateKeyPath -PathType Leaf)) {
    throw "The repository-pinned update verification key is missing."
}
$pinnedUpdateKeyBytes = $null
$pinnedUpdateKey = $null
try {
    $encodedPinnedKey = (Get-Content -LiteralPath $pinnedUpdateKeyPath -Raw).Trim()
    $pinnedUpdateKeyBytes = [Convert]::FromBase64String($encodedPinnedKey)
    $pinnedUpdateKey = [System.Security.Cryptography.ECDsa]::Create()
    $pinnedBytesRead = 0
    $pinnedUpdateKey.ImportSubjectPublicKeyInfo($pinnedUpdateKeyBytes, [ref]$pinnedBytesRead)
    $pinnedParameters = $pinnedUpdateKey.ExportParameters($false)
    if ($pinnedBytesRead -ne $pinnedUpdateKeyBytes.Length -or
        $pinnedUpdateKey.KeySize -ne 256 -or
        $pinnedParameters.Curve.Oid.Value -ne "1.2.840.10045.3.1.7") {
        throw "The repository-pinned update verification key must be exact ECDSA P-256 SPKI DER."
    }
}
catch {
    throw "The repository-pinned update verification key is invalid. $($_.Exception.Message)"
}
finally {
    if ($null -ne $pinnedUpdateKey) {
        $pinnedUpdateKey.Dispose()
    }
    if ($null -ne $pinnedUpdateKeyBytes) {
        [System.Array]::Clear($pinnedUpdateKeyBytes, 0, $pinnedUpdateKeyBytes.Length)
    }
}

& $updateManifestTest

$desktopNotice = Join-Path $repositoryRoot "desktop\THIRD-PARTY-NOTICES.md"
if (-not (Test-Path -LiteralPath $desktopNotice -PathType Leaf)) {
    throw "Desktop third-party notice is missing."
}
foreach ($dependency in @(
    "@xterm/xterm",
    "lucide-react",
    "monaco-editor",
    "react",
    "react-markdown",
    "Microsoft.Data.Sqlite",
    "SQLitePCLRaw.bundle_e_sqlite3",
    "Microsoft.WindowsAppSDK",
    "Microsoft.Windows.SDK.BuildTools"
)) {
    if (-not (Select-String -LiteralPath $desktopNotice -SimpleMatch $dependency -Quiet)) {
        throw "Desktop notice is missing $dependency."
    }
}

foreach ($redistributableDependency in @(
    "Microsoft.Web.WebView2",
    "Microsoft.Windows.AI.MachineLearning",
    "Microsoft.WindowsAppSDK.AI",
    "Microsoft.WindowsAppSDK.Base",
    "Microsoft.WindowsAppSDK.DWrite",
    "Microsoft.WindowsAppSDK.Foundation",
    "Microsoft.WindowsAppSDK.InteractiveExperiences",
    "Microsoft.WindowsAppSDK.ML",
    "Microsoft.WindowsAppSDK.Runtime",
    "Microsoft.WindowsAppSDK.Widgets",
    "Microsoft.WindowsAppSDK.WinUI",
    "Microsoft.NETCore.App.Host.win-arm64",
    "Microsoft.NETCore.App.Host.win-x64",
    "Microsoft.NETCore.App.Runtime.win-arm64",
    "Microsoft.NETCore.App.Runtime.win-x64",
    "System.Numerics.Tensors"
)) {
    if (-not (Select-String -LiteralPath $desktopNotice -SimpleMatch $redistributableDependency -Quiet)) {
        throw "Desktop notice is missing redistributable dependency $redistributableDependency."
    }
}

foreach ($embeddedNoticeText in @(
    "Newtonsoft.Json 13.0.1 - MIT",
    ".NET Runtime uses third-party libraries or other resources that may be",
    "MICROSOFT SOFTWARE LICENSE TERMS",
    "dd07eb178e00c6bba4148457fc00ff77cd4887eb521d504186fe59c9ec8bbe62"
)) {
    if (-not (Select-String -LiteralPath $desktopNotice -SimpleMatch $embeddedNoticeText -Quiet)) {
        throw "Desktop notice does not embed required package notice text '$embeddedNoticeText'."
    }
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("agentdesk-notice-test-" + [guid]::NewGuid().ToString("N"))
try {
    $fixtureWebRoot = Join-Path $fixtureRoot "web"
    $fixtureNodePackage = Join-Path $fixtureWebRoot "node_modules\fixture-npm"
    $fixtureProjectRoot = Join-Path $fixtureRoot "app"
    $fixtureNuGetRoot = Join-Path $fixtureRoot "nuget"
    $fixtureDotNetRoot = Join-Path $fixtureRoot "dotnet"
    $fixtureRidGraphPath = Join-Path $fixtureDotNetRoot "sdk\10.0.302\PortableRuntimeIdentifierGraph.json"
    $fixturePackageRoot = Join-Path $fixtureNuGetRoot "fixture.redistributable\1.0.0"
    foreach ($directory in @(
        $fixtureNodePackage,
        (Join-Path $fixtureProjectRoot "obj"),
        $fixturePackageRoot,
        (Split-Path -Parent $fixtureRidGraphPath)
    )) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    Write-Utf8NoBom -Path $fixtureRidGraphPath -Content '{}'
    Write-Utf8NoBom `
        -Path (Join-Path $fixtureDotNetRoot "LICENSE.txt") `
        -Content "Fixture x64 host legal text."
    Write-Utf8NoBom `
        -Path (Join-Path $fixtureDotNetRoot "ThirdPartyNotices.txt") `
        -Content "Fixture x64 host third-party notice text."

    $fixtureLock = [ordered]@{
        name = "agentdesk-notice-fixture"
        version = "1.0.0"
        lockfileVersion = 3
        requires = $true
        packages = [ordered]@{
            "" = [ordered]@{ dependencies = [ordered]@{ "fixture-npm" = "1.0.0" } }
            "node_modules/fixture-npm" = [ordered]@{ version = "1.0.0"; license = "MIT" }
        }
    }
    $fixtureLockPath = Join-Path $fixtureWebRoot "package-lock.json"
    Write-Utf8NoBom -Path $fixtureLockPath -Content ($fixtureLock | ConvertTo-Json -Depth 10)
    Write-Utf8NoBom `
        -Path (Join-Path $fixtureNodePackage "package.json") `
        -Content '{"name":"fixture-npm","version":"1.0.0","license":"MIT"}'
    Write-Utf8NoBom `
        -Path (Join-Path $fixtureNodePackage "LICENSE") `
        -Content "Fixture npm license text.   `nSecond fixture license line.`t"

    $fixtureProjectPath = Join-Path $fixtureProjectRoot "Fixture.csproj"
    $fixtureProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
  </PropertyGroup>
</Project>
'@
    Write-Utf8NoBom -Path $fixtureProjectPath -Content $fixtureProject

    $fixtureNuspec = @'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Fixture.Redistributable</id>
    <version>1.0.0</version>
    <authors>AgentDesk tests</authors>
    <license type="file">LICENSE.txt</license>
    <projectUrl>https://example.invalid/fixture</projectUrl>
    <description>License fixture.</description>
  </metadata>
</package>
'@
    Write-Utf8NoBom `
        -Path (Join-Path $fixturePackageRoot "fixture.redistributable.nuspec") `
        -Content $fixtureNuspec
    Write-Utf8NoBom `
        -Path (Join-Path $fixturePackageRoot "LICENSE.txt") `
        -Content "Fixture NuGet license text."
    Write-Utf8NoBom `
        -Path (Join-Path $fixturePackageRoot "NOTICE.txt") `
        -Content "Fixture NuGet notice text."

    $fixtureRuntimePackages = @(
        [ordered]@{
            Id = "Microsoft.NETCore.App.Runtime.win-x64"
            LegalText = "Fixture x64 runtime legal text."
        },
        [ordered]@{
            Id = "Microsoft.NETCore.App.Host.win-x64"
            LegalText = "Fixture x64 host legal text."
        },
        [ordered]@{
            Id = "Microsoft.NETCore.App.Runtime.win-arm64"
            LegalText = "Fixture ARM64 runtime legal text."
        },
        [ordered]@{
            Id = "Microsoft.NETCore.App.Host.win-arm64"
            LegalText = "Fixture ARM64 host legal text."
        }
    )
    foreach ($runtimePackage in $fixtureRuntimePackages) {
        $runtimePackageRoot = if ($runtimePackage.Id -eq "Microsoft.NETCore.App.Host.win-x64") {
            Join-Path $fixtureDotNetRoot (Join-Path "packs\$($runtimePackage.Id)" "10.0.10")
        }
        else {
            Join-Path $fixtureNuGetRoot (Join-Path $runtimePackage.Id.ToLowerInvariant() "10.0.10")
        }
        [System.IO.Directory]::CreateDirectory($runtimePackageRoot) | Out-Null
        if ($runtimePackage.Id -eq "Microsoft.NETCore.App.Host.win-x64") {
            $fixtureHostAssetDirectory = Join-Path $runtimePackageRoot "runtimes\win-x64\native"
            [System.IO.Directory]::CreateDirectory($fixtureHostAssetDirectory) | Out-Null
            Write-Utf8NoBom `
                -Path (Join-Path $fixtureHostAssetDirectory "apphost.exe") `
                -Content "Fixture x64 host binary."
            continue
        }
        $runtimePackageNuspec = @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>$($runtimePackage.Id)</id>
    <version>10.0.10</version>
    <authors>AgentDesk tests</authors>
    <license type="file">LICENSE.txt</license>
    <projectUrl>https://example.invalid/$($runtimePackage.Id)</projectUrl>
    <description>Runtime license fixture.</description>
  </metadata>
</package>
"@
        Write-Utf8NoBom `
            -Path (Join-Path $runtimePackageRoot "$($runtimePackage.Id.ToLowerInvariant()).nuspec") `
            -Content $runtimePackageNuspec
        Write-Utf8NoBom `
            -Path (Join-Path $runtimePackageRoot "LICENSE.txt") `
            -Content $runtimePackage.LegalText
    }

    $fixtureAssets = [ordered]@{
        version = 3
        targets = [ordered]@{
            "net10.0/win-x64" = [ordered]@{
                "Fixture.Redistributable/1.0.0" = [ordered]@{
                    type = "package"
                    runtime = [ordered]@{ "lib/net10.0/Fixture.Redistributable.dll" = [ordered]@{} }
                }
            }
        }
        libraries = [ordered]@{
            "Fixture.Redistributable/1.0.0" = [ordered]@{
                type = "package"
                path = "fixture.redistributable/1.0.0"
                files = @("fixture.redistributable.nuspec", "LICENSE.txt", "NOTICE.txt")
            }
        }
        project = [ordered]@{
            restore = [ordered]@{ packagesPath = $fixtureNuGetRoot }
            frameworks = [ordered]@{
                "net10.0" = [ordered]@{
                    runtimeIdentifierGraphPath = $fixtureRidGraphPath
                    downloadDependencies = @(
                        [ordered]@{ name = "Microsoft.NETCore.App.Runtime.win-x64"; version = "[10.0.10, 10.0.10]" },
                        [ordered]@{ name = "Microsoft.NETCore.App.Host.win-x64"; version = "[10.0.10, 10.0.10]" },
                        [ordered]@{ name = "Microsoft.NETCore.App.Runtime.win-arm64"; version = "[10.0.10, 10.0.10]" },
                        [ordered]@{ name = "Microsoft.NETCore.App.Host.win-arm64"; version = "[10.0.10, 10.0.10]" }
                    )
                }
            }
        }
        packageFolders = [ordered]@{ ($fixtureNuGetRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar) = [ordered]@{} }
    }
    $fixtureAssetsPath = Join-Path $fixtureProjectRoot "obj\project.assets.json"
    Write-Utf8NoBom -Path $fixtureAssetsPath -Content ($fixtureAssets | ConvertTo-Json -Depth 20)
    $fixtureOutputPath = Join-Path $fixtureRoot "THIRD-PARTY-NOTICES.md"

    & $noticeGenerator `
        -LockFile $fixtureLockPath `
        -AppProject $fixtureProjectPath `
        -AssetsFile $fixtureAssetsPath `
        -OutputPath $fixtureOutputPath | Out-Null
    foreach ($expectedFixtureText in @(
        "Fixture.Redistributable",
        "Fixture NuGet license text.",
        "Fixture NuGet notice text.",
        "Fixture x64 runtime legal text.",
        "Fixture x64 host legal text.",
        "Fixture x64 host third-party notice text.",
        "Fixture ARM64 runtime legal text.",
        "Fixture ARM64 host legal text."
    )) {
        if (-not (Select-String -LiteralPath $fixtureOutputPath -SimpleMatch $expectedFixtureText -Quiet)) {
            throw "Generated fixture notice is missing '$expectedFixtureText'."
        }
    }
    $fixtureNoticeLines = [System.IO.File]::ReadAllLines($fixtureOutputPath)
    if (@($fixtureNoticeLines | Where-Object { $_ -match '[\x20\x09]+$' }).Count -ne 0) {
        throw "Generated desktop notices must not contain trailing spaces or tabs."
    }

    $fixturePublishedDepsPath = Join-Path $fixtureProjectRoot "published.deps.json"
    $fixturePublishedDeps = [ordered]@{
        runtimeTarget = [ordered]@{ name = ".NETCoreApp,Version=v10.0/win-x64" }
        libraries = [ordered]@{
            "Fixture.Redistributable/1.0.0" = [ordered]@{ type = "package" }
            "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.10" = [ordered]@{ type = "package" }
        }
    }
    Write-Utf8NoBom `
        -Path $fixturePublishedDepsPath `
        -Content ($fixturePublishedDeps | ConvertTo-Json -Depth 10)
    & $noticeGenerator `
        -LockFile $fixtureLockPath `
        -AppProject $fixtureProjectPath `
        -AssetsFile $fixtureAssetsPath `
        -PublishedDepsFile $fixturePublishedDepsPath `
        -OutputPath $fixtureOutputPath | Out-Null

    $fixturePublishedDeps.libraries["Fixture.Missing/9.0.0"] = [ordered]@{ type = "package" }
    Write-Utf8NoBom `
        -Path $fixturePublishedDepsPath `
        -Content ($fixturePublishedDeps | ConvertTo-Json -Depth 10)
    $missingPublishedDependencyRejected = $false
    try {
        & $noticeGenerator `
            -LockFile $fixtureLockPath `
            -AppProject $fixtureProjectPath `
            -AssetsFile $fixtureAssetsPath `
            -PublishedDepsFile $fixturePublishedDepsPath `
            -OutputPath $fixtureOutputPath | Out-Null
    }
    catch {
        if (-not $_.Exception.Message.Contains("Published dependency is not covered")) {
            throw "Missing published dependency failed for the wrong reason: $($_.Exception.Message)"
        }
        $missingPublishedDependencyRejected = $true
    }
    if (-not $missingPublishedDependencyRejected) {
        throw "A published package missing from the generated notice closure must fail closed."
    }

    Write-Utf8NoBom `
        -Path (Join-Path $fixturePackageRoot "fixture.redistributable.nuspec") `
        -Content ($fixtureNuspec -replace '<license type="file">LICENSE.txt</license>', '<licenseUrl>https://example.invalid/fixture-license.rtf</licenseUrl>')
    Remove-Item -LiteralPath (Join-Path $fixturePackageRoot "LICENSE.txt") -Force
    $urlOnlyLicenseRejected = $false
    try {
        & $noticeGenerator `
            -LockFile $fixtureLockPath `
            -AppProject $fixtureProjectPath `
            -AssetsFile $fixtureAssetsPath `
            -OutputPath $fixtureOutputPath | Out-Null
    }
    catch {
        if (-not $_.Exception.Message.Contains("repository-pinned legal text")) {
            throw "URL-only NuGet license failed for the wrong reason: $($_.Exception.Message)"
        }
        $urlOnlyLicenseRejected = $true
    }
    if (-not $urlOnlyLicenseRejected) {
        throw "URL-only NuGet license metadata must fail closed without repository-pinned legal text."
    }

    $fixtureLegalRoot = Join-Path $fixtureRoot "legal"
    [System.IO.Directory]::CreateDirectory($fixtureLegalRoot) | Out-Null
    $fixturePinnedLicensePath = Join-Path $fixtureLegalRoot "fixture-license.rtf"
    Write-Utf8NoBom -Path $fixturePinnedLicensePath -Content "Fixture repository-pinned legal text."
    $fixturePinnedLicenseHash = (Get-FileHash -LiteralPath $fixturePinnedLicensePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $fixtureLicenseManifestPath = Join-Path $fixtureLegalRoot "manifest.json"
    $fixtureLicenseManifest = [ordered]@{
        version = 1
        entries = @(
            [ordered]@{
                packageId = "Fixture.Redistributable"
                packageVersion = "1.0.0"
                licenseUrl = "https://example.invalid/fixture-license.rtf"
                path = "fixture-license.rtf"
                sha256 = $fixturePinnedLicenseHash
            }
        )
    }
    Write-Utf8NoBom `
        -Path $fixtureLicenseManifestPath `
        -Content ($fixtureLicenseManifest | ConvertTo-Json -Depth 10)

    & $noticeGenerator `
        -LockFile $fixtureLockPath `
        -AppProject $fixtureProjectPath `
        -AssetsFile $fixtureAssetsPath `
        -LicenseManifest $fixtureLicenseManifestPath `
        -OutputPath $fixtureOutputPath | Out-Null
    foreach ($expectedPinnedLicenseText in @(
        "Fixture repository-pinned legal text.",
        "https://example.invalid/fixture-license.rtf",
        $fixturePinnedLicenseHash
    )) {
        if (-not (Select-String -LiteralPath $fixtureOutputPath -SimpleMatch $expectedPinnedLicenseText -Quiet)) {
            throw "Generated fixture notice is missing repository-pinned license evidence '$expectedPinnedLicenseText'."
        }
    }

    $fixtureLicenseManifest.entries[0].sha256 = ("0" * 64)
    Write-Utf8NoBom `
        -Path $fixtureLicenseManifestPath `
        -Content ($fixtureLicenseManifest | ConvertTo-Json -Depth 10)
    $badPinnedLicenseHashRejected = $false
    try {
        & $noticeGenerator `
            -LockFile $fixtureLockPath `
            -AppProject $fixtureProjectPath `
            -AssetsFile $fixtureAssetsPath `
            -LicenseManifest $fixtureLicenseManifestPath `
            -OutputPath $fixtureOutputPath | Out-Null
    }
    catch {
        if (-not $_.Exception.Message.Contains("SHA-256 mismatch")) {
            throw "Bad repository-pinned legal text hash failed for the wrong reason: $($_.Exception.Message)"
        }
        $badPinnedLicenseHashRejected = $true
    }
    if (-not $badPinnedLicenseHashRejected) {
        throw "Repository-pinned legal text with a bad SHA-256 must fail closed."
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$workflowSource = (Get-Content -LiteralPath (Join-Path $repositoryRoot ".github\workflows\agentdesk-windows.yml") -Raw) `
    -replace "\r\n?", "`n"
$expectedWorkflowTrigger = @'
on:
  push:
    branches:
      - "main"
    tags:
      - "v*"
  pull_request:
  workflow_dispatch:
'@
if (-not $workflowSource.Contains($expectedWorkflowTrigger)) {
    throw "CI must run for main pushes, version tags, pull requests, and explicit workflow dispatch without duplicating branch pushes."
}
$workflowLines = $workflowSource -split "`n"
for ($lineIndex = 0; $lineIndex -lt $workflowLines.Count; $lineIndex++) {
    $shellLine = $workflowLines[$lineIndex]
    if ($shellLine.Trim() -ne "shell: pwsh") {
        continue
    }

    $shellIndent = $shellLine.Length - $shellLine.TrimStart().Length
    $runIndex = $lineIndex + 1
    while ($runIndex -lt $workflowLines.Count -and
        $workflowLines[$runIndex].Trim() -ne "run: |") {
        $candidate = $workflowLines[$runIndex]
        $candidateIndent = $candidate.Length - $candidate.TrimStart().Length
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and $candidateIndent -lt $shellIndent) {
            break
        }
        $runIndex++
    }
    if ($runIndex -ge $workflowLines.Count -or $workflowLines[$runIndex].Trim() -ne "run: |") {
        continue
    }

    $blockIndent = $shellIndent + 2
    $blockLines = [System.Collections.Generic.List[string]]::new()
    for ($blockIndex = $runIndex + 1; $blockIndex -lt $workflowLines.Count; $blockIndex++) {
        $blockLine = $workflowLines[$blockIndex]
        $currentIndent = $blockLine.Length - $blockLine.TrimStart().Length
        if (-not [string]::IsNullOrWhiteSpace($blockLine) -and $currentIndent -le $shellIndent) {
            break
        }
        if ($blockLine.Length -ge $blockIndent) {
            $blockLines.Add($blockLine.Substring($blockIndent))
        }
        else {
            $blockLines.Add("")
        }
    }

    $tokens = $null
    $parseErrors = $null
    $parseSource = [System.Text.RegularExpressions.Regex]::Replace(
        ($blockLines -join "`n"),
        '\$\{\{.*?\}\}',
        'github_expression')
    [void][System.Management.Automation.Language.Parser]::ParseInput(
        $parseSource,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) {
        $details = $parseErrors | ForEach-Object {
            "Line $($_.Extent.StartLineNumber): $($_.Message)"
        }
        throw "The AgentDesk workflow contains an invalid pwsh run block near line $($lineIndex + 1). $($details -join '; ')"
    }
}
if (($workflowSource.Split("path: artifacts/input", [System.StringSplitOptions]::None).Count - 1) -lt 2 -or
    $workflowSource.Contains("path: artifacts/input/portable")) {
    throw "Windows SBOM generation must scan the complete package input, including update-staging."
}
if (-not $workflowSource.Contains("runner: ubuntu-22.04-arm")) {
    throw "ARM64 WSL sidecars must build on the Ubuntu 22.04 ARM runner."
}
if (-not $workflowSource.Contains("Verify-AgentDeskLinuxBinary.sh")) {
    throw "The Linux sidecar job must verify its ELF architecture and glibc requirement."
}

$npmInstallIndex = $workflowSource.IndexOf("working-directory: desktop/web`n        run: npm ci", [System.StringComparison]::Ordinal)
$dotnetRestoreIndex = $workflowSource.IndexOf("dotnet restore ./desktop/src/AgentDesk.App/AgentDesk.App.csproj", [System.StringComparison]::Ordinal)
$noticeGenerationIndex = $workflowSource.IndexOf("./scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1", [System.StringComparison]::Ordinal)
$noticeDiffIndex = $workflowSource.IndexOf("git diff --exit-code -- desktop/THIRD-PARTY-NOTICES.md", [System.StringComparison]::Ordinal)
if ($npmInstallIndex -lt 0 -or
    $dotnetRestoreIndex -le $npmInstallIndex -or
    $noticeGenerationIndex -le $dotnetRestoreIndex -or
    $noticeDiffIndex -le $noticeGenerationIndex) {
    throw "CI must restore npm/.NET dependencies, regenerate the desktop notices, and fail on a committed diff in that order."
}

$lastPackageBuildIndex = $workflowSource.LastIndexOf(
    "./scripts/agentdesk/Build-AgentDeskPackage.ps1",
    [System.StringComparison]::Ordinal)
$postPackageClosureIndex = $workflowSource.IndexOf(
    "- name: Verify packaged dependency closure",
    [System.StringComparison]::Ordinal)
$uploadPackageInputsIndex = $workflowSource.IndexOf(
    "- name: Upload package inputs",
    [System.StringComparison]::Ordinal)
if ($lastPackageBuildIndex -lt 0 -or
    $postPackageClosureIndex -le $lastPackageBuildIndex -or
    $uploadPackageInputsIndex -le $postPackageClosureIndex) {
    throw "CI must verify the packaged dependency closure after every package build branch and before upload."
}
$postPackageClosureSource = $workflowSource.Substring(
    $postPackageClosureIndex,
    $uploadPackageInputsIndex - $postPackageClosureIndex)
foreach ($requiredClosureCheck in @(
    './scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1',
    '-PublishedDepsFile',
    'package-input-${{ matrix.architecture }}/portable/AgentDesk.App.deps.json',
    'git diff --exit-code -- desktop/THIRD-PARTY-NOTICES.md',
    'Packaged dependency closure verification failed.'
)) {
    if (-not $postPackageClosureSource.Contains($requiredClosureCheck)) {
        throw "The post-package dependency closure check is missing '$requiredClosureCheck'."
    }
}

$linuxJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  linux-sidecar:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:)')
if (-not $linuxJob.Success) {
    throw "The AgentDesk workflow must define a linux-sidecar job."
}
$linuxJobSource = $linuxJob.Groups["body"].Value
$agentDeskContractCommand = "cargo test --locked -p xai-grok-shell --test agentdesk_contract -- --test-threads=1"
$memoryTestCommand = "cargo test --locked -p xai-grok-memory"
$sessionTransferCommand = "cargo test --locked -p xai-grok-shell --test agentdesk_session_transfer -- --test-threads=1"
foreach ($requiredLinuxTest in @(
    "SANDBOX_E2E_REQUIRE_ENFORCEMENT=1 cargo test --locked -p xai-grok-sandbox",
    $agentDeskContractCommand,
    $memoryTestCommand,
    $sessionTransferCommand
)) {
    if (-not $linuxJobSource.Contains($requiredLinuxTest)) {
        throw "The Linux sidecar job must run '$requiredLinuxTest'."
    }
}
foreach ($requiredStrictHealthCommand in @(
    "python3 ./scripts/agentdesk/Test-AgentDeskLinuxStrictHealth.py",
    "./scripts/agentdesk/Verify-AgentDeskLinuxStrictHealth.py"
)) {
    if (-not $linuxJobSource.Contains($requiredStrictHealthCommand)) {
        throw "The Linux sidecar job must run '$requiredStrictHealthCommand'."
    }
}
if ($linuxJobSource.Contains("continue-on-error: true")) {
    throw "Linux sidecar verification must not be advisory."
}

$windowsJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  windows-build:\r?\n(?<body>.*?)(?=^  assemble-release:)')
if (-not $windowsJob.Success) {
    throw "The AgentDesk workflow must define a windows-build job."
}
$windowsJobSource = $windowsJob.Groups["body"].Value
if (-not $windowsJobSource.Contains($agentDeskContractCommand)) {
    throw "The Windows build job must run '$agentDeskContractCommand'."
}
$windowsContractCheck = [System.Text.RegularExpressions.Regex]::Escape($agentDeskContractCommand) +
    '\r?\n\s+if \(\$LASTEXITCODE -ne 0\) \{ throw "AgentDesk contract tests failed\." \}'
if (-not [System.Text.RegularExpressions.Regex]::IsMatch($windowsJobSource, $windowsContractCheck)) {
    throw "The Windows AgentDesk contract test must check LASTEXITCODE explicitly."
}

foreach ($requiredWindowsX64Step in @(
    @{
        Name = "Test AgentDesk memory on Windows x64"
        Command = $memoryTestCommand
    },
    @{
        Name = "Test AgentDesk session transfer on Windows x64"
        Command = $sessionTransferCommand
    },
    @{
        Name = "Check pager library on Windows x64"
        Command = "cargo check --locked -p xai-grok-pager --lib"
    },
    @{
        Name = "Verify Cloud formatting on Windows x64"
        Command = "dotnet format ./cloud/tests/AgentDesk.Cloud.Tests/AgentDesk.Cloud.Tests.csproj --verify-no-changes --no-restore"
    },
    @{
        Name = "Test Cloud on Windows x64"
        Command = "dotnet test ./cloud/tests/AgentDesk.Cloud.Tests/AgentDesk.Cloud.Tests.csproj --configuration Release"
    }
)) {
    $step = [System.Text.RegularExpressions.Regex]::Match(
        $windowsJobSource,
        '(?ms)^      - name: ' +
            [System.Text.RegularExpressions.Regex]::Escape($requiredWindowsX64Step.Name) +
            '\r?\n(?<body>.*?)(?=^      - name:|\z)')
    if (-not $step.Success) {
        throw "The Windows build job is missing '$($requiredWindowsX64Step.Name)'."
    }
    $stepSource = $step.Groups["body"].Value
    foreach ($requiredStepSource in @(
        "if: matrix.architecture == 'x64'",
        "shell: pwsh",
        "Remove-Item Env:GROK_THIRD_PARTY_API_KEY -ErrorAction SilentlyContinue",
        $requiredWindowsX64Step.Command,
        'if ($LASTEXITCODE -ne 0)'
    )) {
        if (-not $stepSource.Contains($requiredStepSource)) {
            throw "The Windows x64 step '$($requiredWindowsX64Step.Name)' is missing '$requiredStepSource'."
        }
    }
}
if (-not $windowsJobSource.Contains(
        "dotnet format ./cloud/src/AgentDesk.Cloud/AgentDesk.Cloud.csproj --verify-no-changes --no-restore")) {
    throw "The Windows x64 Cloud formatting step must cover the Cloud server project."
}

$dotnetFormatCommand = "dotnet format desktop/AgentDesk.sln --verify-no-changes --no-restore"
if (-not $windowsJobSource.Contains($dotnetFormatCommand)) {
    throw "The Windows build job must run '$dotnetFormatCommand'."
}

$packagedCdpStep = [System.Text.RegularExpressions.Regex]::Match(
    $windowsJobSource,
    '(?ms)^      - name: Smoke-test packaged WebView2 surfaces over CDP\r?\n(?<body>.*?)(?=^      - name:)')
if (-not $packagedCdpStep.Success) {
    throw "The Windows build job must smoke-test packaged WebView2 surfaces over CDP."
}
$packagedCdpStepSource = $packagedCdpStep.Groups["body"].Value
if ($packagedCdpStepSource.Contains("matrix.architecture == 'x64'")) {
    throw "The packaged WebView2 CDP smoke must run on both native Windows matrix architectures."
}
foreach ($requiredPackagedCdpSource in @(
    'package-input-${{ matrix.architecture }}/portable/AgentDesk.App.exe',
    'dotnet publish ./desktop/tools/AgentDesk.ProcessJobLauncher/AgentDesk.ProcessJobLauncher.csproj',
    'cdp-launcher-${{ matrix.architecture }}',
    'AgentDesk.ProcessJobLauncher.exe',
    'node ./scripts/agentdesk/Test-AgentDeskWebViewCdp.mjs',
    '--launcher $jobLauncher'
)) {
    if (-not $packagedCdpStepSource.Contains($requiredPackagedCdpSource)) {
        throw "The packaged WebView2 CDP smoke is missing '$requiredPackagedCdpSource'."
    }
}

foreach ($forbiddenShellTest in @(
    "cargo test --locked -p xai-grok-shell --lib agentdesk_extensions_report_versioned_sandbox_health",
    "cargo test --locked -p xai-grok-shell --lib api_key_persistence",
    "cargo test --locked -p xai-grok-shell --lib desktop_api_key_auth",
    "cargo test --locked -p xai-grok-shell --lib desktop_api_key_capture",
    "cargo test --locked -p xai-grok-shell --lib api_key_auth_keeps_existing_environment"
)) {
    if ($workflowSource.Contains($forbiddenShellTest)) {
        throw "The AgentDesk workflow must not use the private shell test entry point '$forbiddenShellTest'."
    }
}

if (-not $workflowSource.Contains(
        "if: github.event_name == 'push' && github.ref_type == 'tag' && env.SIGN_MSIX != 'true'")) {
    throw "Tag builds must fail closed when either MSIX signing secret is missing."
}
if (-not $workflowSource.Contains(
        "if: env.SIGN_MSIX != 'true' && (github.event_name != 'push' || github.ref_type != 'tag')")) {
    throw "Unsigned MSIX package inputs must never be built for a tag."
}
if (-not $workflowSource.Contains(
        "SIGN_MSIX: `${{ github.event_name == 'push' && github.ref_type == 'tag'")) {
    throw "Only push-triggered tags may receive AgentDesk release signing credentials."
}

foreach ($requiredRollbackWorkflow in @(
    "gh release download",
    "New-AgentDeskRollbackBundle.ps1",
    "-RequireSignedMsix",
    "ConvertTo-AgentDeskReleaseVersion",
    'Where-Object { $_.ComparableVersion -lt $currentComparableVersion }',
    'Sort-Object ComparableVersion -Descending'
)) {
    if (-not $workflowSource.Contains($requiredRollbackWorkflow)) {
        throw "The tag release workflow is missing rollback requirement '$requiredRollbackWorkflow'."
    }
}
if ($workflowSource.Contains('Sort-Object { [DateTimeOffset]::Parse($_.publishedAt) }')) {
    throw "Rollback selection must use supported release-version ordering, not publication time."
}

$cloudJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  cloud-tests:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:)')
if (-not $cloudJob.Success -or
    -not $cloudJob.Groups["body"].Value.Contains(
        "dotnet test ./cloud/tests/AgentDesk.Cloud.Tests/AgentDesk.Cloud.Tests.csproj --configuration Release")) {
    throw "CI must run the self-hosted cloud integration tests in Release configuration."
}
$cloudMaintenanceJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  cloud-maintenance:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:)')
if (-not $cloudMaintenanceJob.Success) {
    throw "CI must define a cloud-maintenance job for offline database recovery E2E coverage."
}
$cloudMaintenanceJobSource = $cloudMaintenanceJob.Groups["body"].Value
foreach ($requiredCloudMaintenanceSource in @(
    "runs-on: windows-2025",
    "dotnet restore ./cloud/src/AgentDesk.Cloud/AgentDesk.Cloud.csproj",
    "./scripts/agentdesk/Test-AgentDeskCloudDatabaseMaintenance.ps1"
)) {
    if (-not $cloudMaintenanceJobSource.Contains($requiredCloudMaintenanceSource)) {
        throw "The Cloud maintenance E2E job is missing '$requiredCloudMaintenanceSource'."
    }
}
$releaseJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  github-release:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)')
if (-not $releaseJob.Success -or
    -not $releaseJob.Groups["body"].Value.Contains("- cloud-tests")) {
    throw "Tag publication must wait for the self-hosted cloud integration tests."
}
if (-not $releaseJob.Groups["body"].Value.Contains("- cloud-maintenance")) {
    throw "Tag publication must wait for the Cloud database maintenance E2E job."
}
$releaseJobSource = $releaseJob.Groups["body"].Value
if (-not $releaseJobSource.Contains(
        "if: github.event_name == 'push' && github.ref_type == 'tag'")) {
    throw "Versioned GitHub Release publication must be limited to push-triggered tags."
}
if ($releaseJobSource.Contains("--clobber")) {
    throw "Published GitHub Release assets must be immutable; --clobber is forbidden."
}
foreach ($requiredFifoGate in @(
    "actions: read",
    "- name: Wait for earlier tag workflows",
    "gh run list",
    "--workflow agentdesk-windows.yml",
    "--event push",
    "--limit 1000",
    "--json number,headBranch,headSha,status,conclusion,event,databaseId",
    "Test-IsAgentDeskTagRun",
    "git/matching-refs/tags",
    'refs/tags/$tagName',
    "git/tags/",
    '[System.StringComparison]::OrdinalIgnoreCase',
    '$_.number -lt $currentRunNumber',
    '$_.event -ceq "push"',
    '$_.status -ne "completed"',
    '$_.status -eq "completed"',
    '$_.conclusion -cne "success"',
    '$_.headBranch -match',
    "Earlier AgentDesk tag workflow",
    "Rerun it successfully or explicitly abandon it",
    "Start-Sleep -Seconds 30",
    "Timed out waiting for earlier AgentDesk tag workflows"
)) {
    if (-not $releaseJobSource.Contains($requiredFifoGate)) {
        throw "Tag publication is missing FIFO wait requirement '$requiredFifoGate'."
    }
}
$fifoGateIndex = $releaseJobSource.IndexOf(
    "- name: Wait for earlier tag workflows",
    [System.StringComparison]::Ordinal)
$releaseCheckoutIndex = $releaseJobSource.IndexOf(
    "- name: Check out release scripts",
    [System.StringComparison]::Ordinal)
if ($fifoGateIndex -lt 0 -or
    $releaseCheckoutIndex -lt 0 -or
    $fifoGateIndex -ge $releaseCheckoutIndex) {
    throw "The FIFO tag wait must run before release checkout or publication side effects."
}
foreach ($requiredTagReachabilityGuard in @(
    'fetch-depth: 0',
    'refs/heads/main:refs/remotes/origin/main',
    'git merge-base --is-ancestor $tagCommit origin/main'
)) {
    if (-not $releaseJobSource.Contains($requiredTagReachabilityGuard)) {
        throw "Tag publication must prove the tag commit is reachable from origin/main using '$requiredTagReachabilityGuard'."
    }
}
foreach ($requiredReleasePublicationGuard in @(
    '--json isDraft',
    'if (-not [bool]$existingRelease.isDraft)',
    'gh release delete $releaseTag --yes',
    '--draft',
    '$isPrerelease',
    '--prerelease'
)) {
    if (-not $releaseJobSource.Contains($requiredReleasePublicationGuard)) {
        throw "Tag publication is missing immutable draft-release behavior '$requiredReleasePublicationGuard'."
    }
}
if ($releaseJobSource.Contains('gh release edit $releaseTag --draft=false')) {
    throw "The versioned GitHub Release must remain draft until fixed feeds advance."
}
if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
        $releaseJobSource,
        '(?ms)if \(\$isPrerelease\) \{\s+\$createArguments \+= "--prerelease"\s+\}')) {
    throw "Prerelease tags must add --prerelease conditionally when the draft is created."
}
foreach ($requiredSignedRollbackCheck in @(
    "runs-on: windows-2025",
    "New-AgentDeskRollbackBundle.ps1",
    'PREVIOUS_MSIX_SIGNER_THUMBPRINT: ${{ vars.AGENTDESK_PREVIOUS_MSIX_SIGNER_THUMBPRINT }}',
    'PREVIOUS_UPDATE_PUBLIC_KEY_SHA256: ${{ vars.AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256 }}',
    'Tag rollback requires AGENTDESK_PREVIOUS_MSIX_SIGNER_THUMBPRINT',
    'Tag rollback requires AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256',
    '-ExpectedPreviousMsixSignerThumbprint $env:PREVIOUS_MSIX_SIGNER_THUMBPRINT',
    '-ExpectedPreviousUpdatePublicKeySha256 $env:PREVIOUS_UPDATE_PUBLIC_KEY_SHA256',
    '--pattern "AgentDesk-update-manifest.json"',
    '--pattern "AgentDesk-update-manifest.json.sig"',
    '--pattern "AgentDesk-update-public-key.spki"',
    'Where-Object { $_.ComparableVersion -ge $currentComparableVersion }',
    'Refusing to create an out-of-order AgentDesk Release',
    '-PreviousUpdateManifestPath (Join-Path $downloadRoot "AgentDesk-update-manifest.json")',
    '-PreviousUpdateManifestSignaturePath (Join-Path $downloadRoot "AgentDesk-update-manifest.json.sig")',
    '-TrustedUpdatePublicKeyPath (Join-Path $downloadRoot "AgentDesk-update-public-key.spki")'
)) {
    if (-not $releaseJobSource.Contains($requiredSignedRollbackCheck)) {
        throw "Tag rollback publication is missing cryptographic check '$requiredSignedRollbackCheck'."
    }
}
$outOfOrderReleaseGuardIndex = $releaseJobSource.IndexOf(
    "Refusing to create an out-of-order AgentDesk Release",
    [System.StringComparison]::Ordinal)
$firstReleaseMarkerIndex = $releaseJobSource.IndexOf(
    "NO-PREVIOUS-ROLLBACK.txt",
    [System.StringComparison]::Ordinal)
if ($outOfOrderReleaseGuardIndex -lt 0 -or
    $firstReleaseMarkerIndex -lt 0 -or
    $outOfOrderReleaseGuardIndex -ge $firstReleaseMarkerIndex) {
    throw "Out-of-order tag rejection must run before the first-release rollback branch."
}
$rollbackStep = [System.Text.RegularExpressions.Regex]::Match(
    $releaseJobSource,
    '(?ms)- name: Prepare verified previous-release rollback bundle\r?\n(?<body>.*?)(?=\r?\n      - name:)')
if (-not $rollbackStep.Success) {
    throw "The release workflow is missing the previous-release rollback step."
}
if ($rollbackStep.Groups["body"].Value.Contains(
        "desktop/update/AgentDesk-update-public-key.spki.base64") -or
    $rollbackStep.Groups["body"].Value.Contains(
        "agentdesk-rollback-update-public-key.spki")) {
    throw "Rollback verification must not reuse the current release update public key."
}
if ($releaseJobSource.Contains(
        'TRUSTED_MSIX_SIGNER_THUMBPRINT: ${{ vars.AGENTDESK_MSIX_SIGNER_THUMBPRINT }}') -or
    $releaseJobSource.Contains(
        '-ExpectedMsixSignerThumbprint $env:TRUSTED_MSIX_SIGNER_THUMBPRINT')) {
    throw "Tag rollback publication must not reuse the current release MSIX signer pin."
}
if ($releaseJobSource.Contains("Verify-AgentDeskMsixSignature.ps1")) {
    throw "Tag rollback publication must verify the immutable staged MSIX inside the rollback bundler."
}
if ($releaseJobSource.Contains('-ExpectedPublisher $publisher') -or
    $releaseJobSource.Contains('$publisher = [string]$manifest.Package.Identity.Publisher')) {
    throw "Rollback verification must not trust a Publisher value read from the package being verified."
}

$feedJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  publish-update-feed:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)')
if (-not $feedJob.Success) {
    throw "The release workflow must publish fixed signed update feeds."
}
$feedJobSource = $feedJob.Groups["body"].Value
if (-not $feedJobSource.Contains(
        "if: github.event_name == 'push' && github.ref_type == 'tag'")) {
    throw "Fixed update-feed publication must be limited to push-triggered tags."
}
foreach ($requiredFeedPublication in @(
    'needs: github-release',
    'AgentDesk-signed-update-metadata',
    'Confirm-AgentDeskUpdateFeedAdvance.ps1',
    'desktop/update/AgentDesk-update-public-key.spki.base64',
    'PREVIOUS_UPDATE_PUBLIC_KEY_SHA256: ${{ vars.AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256 }}',
    'AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256',
    '-PreviousTrustedPublicKeyPath',
    'gh release download $feedTag',
    '$feedTags = @("update-prerelease")',
    '$feedTags += "update-stable"',
    'AgentDesk-update-manifest.json',
    'AgentDesk-update-manifest.json.sig',
    'AgentDesk-updater-manifest.json',
    'AgentDesk-updater-manifest.json.sig',
    'gh release upload $feedTag @metadataFiles --clobber',
    '--prerelease'
)) {
    if (-not $feedJobSource.Contains($requiredFeedPublication)) {
        throw "The fixed update feed job is missing '$requiredFeedPublication'."
    }
}
if ($feedJobSource.Contains('/releases/latest/')) {
    throw "Fixed update feeds must not depend on GitHub's stable-only latest Release alias."
}
$feedValidationIndex = $feedJobSource.LastIndexOf(
    "Confirm-AgentDeskUpdateFeedAdvance.ps1",
    [System.StringComparison]::Ordinal)
$feedClobberIndex = $feedJobSource.IndexOf(
    'gh release upload $feedTag @metadataFiles --clobber',
    [System.StringComparison]::Ordinal)
if ($feedValidationIndex -lt 0 -or
    $feedClobberIndex -lt 0 -or
    $feedValidationIndex -ge $feedClobberIndex) {
    throw "Existing signed feed metadata must be verified before any fixed-feed clobber."
}

$finalizeReleaseJob = [System.Text.RegularExpressions.Regex]::Match(
    $workflowSource,
    '(?ms)^  finalize-github-release:\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)')
if (-not $finalizeReleaseJob.Success) {
    throw "The release workflow must publish the versioned draft only after fixed feeds advance."
}
$finalizeReleaseJobSource = $finalizeReleaseJob.Groups["body"].Value
if (-not $finalizeReleaseJobSource.Contains(
        "if: github.event_name == 'push' && github.ref_type == 'tag'")) {
    throw "Final GitHub Release publication must be limited to push-triggered tags."
}
foreach ($requiredFinalPublication in @(
    'needs: publish-update-feed',
    '--json isDraft',
    'if (-not [bool]$release.isDraft)',
    'git fetch --force --no-tags origin "+refs/tags/${releaseTag}:$verificationRef"',
    'git rev-parse "$verificationRef^{commit}"',
    '[System.StringComparison]::OrdinalIgnoreCase',
    'gh release edit $releaseTag --draft=false'
)) {
    if (-not $finalizeReleaseJobSource.Contains($requiredFinalPublication)) {
        throw "Final tag publication is missing '$requiredFinalPublication'."
    }
}
$remoteTagResolutionIndex = $finalizeReleaseJobSource.IndexOf(
    'git fetch --force --no-tags origin "+refs/tags/${releaseTag}:$verificationRef"',
    [System.StringComparison]::Ordinal)
$finalReleaseEditIndex = $finalizeReleaseJobSource.IndexOf(
    'gh release edit $releaseTag --draft=false',
    [System.StringComparison]::Ordinal)
if ($remoteTagResolutionIndex -lt 0 -or
    $finalReleaseEditIndex -lt 0 -or
    $remoteTagResolutionIndex -ge $finalReleaseEditIndex) {
    throw "The remote lightweight or annotated tag must be re-resolved immediately before publication."
}

foreach ($requiredConcurrencyContract in @(
    "github.ref_type == 'tag'",
    "github.run_id",
    "format('agentdesk-{0}-{1}', github.workflow, github.ref)",
    'cancel-in-progress: ${{ github.ref_type != ''tag'' }}'
)) {
    if (-not $workflowSource.Contains($requiredConcurrencyContract)) {
        throw "The workflow must isolate tag builds and retain ref-keyed branch concurrency using '$requiredConcurrencyContract'."
    }
}
if ($workflowSource.Contains("'agentdesk-tag-release'")) {
    throw "Tag runs must not rely on a lossy shared top-level concurrency pending slot."
}

foreach ($releaseGuide in @(
    (Join-Path $repositoryRoot "docs\RELEASING.md"),
    (Join-Path $repositoryRoot "docs\RELEASING.zh-CN.md")
)) {
    $releaseGuideSource = Get-Content -LiteralPath $releaseGuide -Raw
    foreach ($requiredPreviousUpdateKeyGuidance in @(
        "AGENTDESK_PREVIOUS_UPDATE_PUBLIC_KEY_SHA256",
        "AgentDesk-update-public-key.spki"
    )) {
        if (-not $releaseGuideSource.Contains($requiredPreviousUpdateKeyGuidance)) {
            throw "Release guidance '$releaseGuide' is missing previous update-key rotation contract '$requiredPreviousUpdateKeyGuidance'."
        }
    }
    foreach ($requiredFixedFeedGuidance in @(
        "update-stable",
        "update-prerelease",
        "draft",
        "retry",
        "fail-closed"
    )) {
        if (-not $releaseGuideSource.Contains($requiredFixedFeedGuidance)) {
            throw "Release guidance '$releaseGuide' is missing fixed-feed recovery guidance '$requiredFixedFeedGuidance'."
        }
    }
}

$buildGuideSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "docs\BUILD-AND-TEST.md") -Raw
foreach ($requiredEvidenceBoundary in @(
    "Do not claim strict sandbox enforcement from a Windows-only or skipped test.",
    "These results do not prove a signed public MSIX",
    '`WslStrict`'
)) {
    if (-not $buildGuideSource.Contains($requiredEvidenceBoundary)) {
        throw "Build guidance must preserve the WSL/skip evidence boundary '$requiredEvidenceBoundary'."
    }
}

$buildSource = Get-Content -LiteralPath $buildScript -Raw
if (-not $buildSource.Contains("Verify-AgentDeskMsixSignature.ps1")) {
    throw "Signed MSIX packages must run the cryptographic signature verifier."
}
if ($buildSource.Contains('ExpectedPublisher = [string]$packagedManifestXml.Package.Identity.Publisher')) {
    throw "Package verification must not trust a Publisher value read from the package being verified."
}
foreach ($requiredWslArchitectureCheck in @(
    'Test-AgentDeskLinuxEngineArchitecture.ps1',
    '& $linuxEngineArchitectureVerifierPath -Path $WslEnginePath -Architecture $Architecture'
)) {
    if (-not $buildSource.Contains($requiredWslArchitectureCheck)) {
        throw "The package builder is missing WSL sidecar architecture verification '$requiredWslArchitectureCheck'."
    }
}

$signatureVerifierSource = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot "Verify-AgentDeskMsixSignature.ps1") -Raw
foreach ($requiredSignerIdentityCheck in @(
    '$TrustedPublisher = "CN=AgentDesk"',
    'AppxManifest.xml',
    'ExpectedThumbprint',
    'SignerCertificate.Thumbprint'
)) {
    if (-not $signatureVerifierSource.Contains($requiredSignerIdentityCheck)) {
        throw "The MSIX verifier is missing pinned signer identity behavior '$requiredSignerIdentityCheck'."
    }
}
if ($signatureVerifierSource.Contains('[Parameter(Mandatory)][string]$ExpectedPublisher')) {
    throw "The MSIX verifier must not allow callers to replace the repository-pinned Publisher."
}

foreach ($requiredBuildInput in @(
    'desktop\THIRD-PARTY-NOTICES.md',
    'desktop\THIRD-PARTY-SOURCE-NOTICE.zh-CN.md',
    '-p:AgentDeskDesktopNoticesPath=$desktopNoticesPath',
    '-p:AgentDeskSourceNoticeZhCnPath=$sourceNoticeZhCnPath'
)) {
    if (-not $buildSource.Contains($requiredBuildInput)) {
        throw "The package builder is missing required desktop legal input '$requiredBuildInput'."
    }
}

foreach ($requiredUpdaterBuildSource in @(
    'AgentDesk.Updater\AgentDesk.Updater.csproj',
    'PublishSingleFile=true',
    'IncludeNativeLibrariesForSelfExtract=true',
    'update-staging',
    'DEVELOPMENT-ONLY.txt'
)) {
    if (-not $buildSource.Contains($requiredUpdaterBuildSource)) {
        throw "The package builder is missing updater release behavior '$requiredUpdaterBuildSource'."
    }
}

$finalizeSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Finalize-AgentDeskPackage.ps1") -Raw
foreach ($requiredUpdaterFinalizeSource in @(
    'update-staging',
    'AgentDesk.Updater.exe',
    '-updater.zip',
    'Compress-Archive',
    'DEVELOPMENT-ONLY.txt',
    '-UPDATE-STATUS.txt'
)) {
    if (-not $finalizeSource.Contains($requiredUpdaterFinalizeSource)) {
        throw "The release finalizer is missing updater artifact behavior '$requiredUpdaterFinalizeSource'."
    }
}

foreach ($requiredUpdateWorkflowSource in @(
    'AGENTDESK_UPDATE_ECDSA_PRIVATE_KEY_PKCS8_BASE64',
    'desktop/update/AgentDesk-update-public-key.spki.base64',
    'New-AgentDeskUpdateManifest.ps1',
    'AgentDesk-update-manifest.json.sig',
    'AgentDesk-updater-manifest.json.sig',
    '-X64UpdaterPath $x64Updaters[0].FullName',
    '-Arm64UpdaterPath $arm64Updaters[0].FullName',
    '-updater.zip',
    '-PrivateKeyEnvironmentVariable "UPDATE_PRIVATE_KEY_PKCS8_BASE64"',
    '-PublicKeyPath $publicKeyPath',
    'Tag releases require the update signing private key',
    'signed-update-metadata',
    '-UPDATE-STATUS.txt',
    'Recalculate architecture checksums after status promotion'
)) {
    if (-not $workflowSource.Contains($requiredUpdateWorkflowSource)) {
        throw "The release workflow is missing signed update metadata behavior '$requiredUpdateWorkflowSource'."
    }
}
if ($workflowSource.Contains('AGENTDESK_UPDATE_ECDSA_PUBLIC_KEY_SPKI_BASE64')) {
    throw "The release workflow must use the repository-pinned update public key, not a replaceable public-key secret."
}

$packagingTargetsSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot "AgentDesk.Packaging.targets") -Raw
foreach ($requiredMsixPayload in @(
    '$(AgentDeskDesktopNoticesPath)',
    '<TargetPath>THIRD-PARTY-NOTICES.md</TargetPath>',
    '$(AgentDeskSourceNoticeZhCnPath)',
    '<TargetPath>THIRD-PARTY-SOURCE-NOTICE.zh-CN.md</TargetPath>',
    '$(TargetDir)App.xbf',
    '$(TargetDir)MainWindow.xbf'
)) {
    if (-not $packagingTargetsSource.Contains($requiredMsixPayload)) {
        throw "The MSIX payload is missing required desktop legal input '$requiredMsixPayload'."
    }
}

$appProjectSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "desktop\src\AgentDesk.App\AgentDesk.App.csproj") -Raw
foreach ($requiredPortableXamlPayload in @(
    'AgentDeskCopyCompiledXamlToPublish',
    '$(TargetDir)*.xbf',
    '$(TargetDir)$(AssemblyName).pri',
    'DestinationFolder="$(PublishDir)"'
)) {
    if (-not $appProjectSource.Contains($requiredPortableXamlPayload)) {
        throw "The portable publish target is missing WinUI runtime payload '$requiredPortableXamlPayload'."
    }
}

Write-Host "AgentDesk release script tests passed."
