<#
.SYNOPSIS
Verifies that release packaging rejects a native sidecar from another revision.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$buildScript = Join-Path $PSScriptRoot "Build-AgentDeskPackage.ps1"
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-engine-revision-test-" + [guid]::NewGuid().ToString("N"))
$previousPath = $env:PATH
$previousMode = $env:AGENTDESK_FAKE_ENGINE_MODE
$previousRevision = $env:AGENTDESK_FAKE_ENGINE_REVISION

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Content
    )

    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Set-PeStackReserve {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][uint64]$StackReserveBytes
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5a4d) {
        throw "Fake engine is not a PE executable: $Path"
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    $stackReserveOffset = $peOffset + 24 + 72
    if ($peOffset -lt 0 -or $stackReserveOffset -gt $bytes.Length - 8) {
        throw "Fake engine has an invalid PE optional header: $Path"
    }
    [BitConverter]::GetBytes($StackReserveBytes).CopyTo($bytes, $stackReserveOffset)
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Invoke-PackageBuild {
    param(
        [Parameter(Mandatory)][string]$EnginePath,
        [Parameter(Mandatory)][ValidateSet("x64", "arm64")][string]$Architecture,
        [Parameter(Mandatory)][string]$SourceRevision,
        [Parameter(Mandatory)][string]$Mode,
        [string]$EngineRevision = $SourceRevision
    )

    $env:AGENTDESK_FAKE_ENGINE_MODE = $Mode
    $env:AGENTDESK_FAKE_ENGINE_REVISION = $EngineRevision
    $outputRoot = Join-Path $fixtureRoot ("output-" + $Mode + "-" + [guid]::NewGuid().ToString("N"))
    & $buildScript `
        -Architecture $Architecture `
        -Mode Portable `
        -Version "0.2.0-ci.7" `
        -NativeEnginePath $EnginePath `
        -OutputRoot $outputRoot `
        -SourceRepository "https://github.com/rkshadow999/AgentDesk" `
        -SourceRevision $SourceRevision | Out-Null
}

function Assert-PackageRejectsEngine {
    param(
        [Parameter(Mandatory)][string]$EnginePath,
        [Parameter(Mandatory)][ValidateSet("x64", "arm64")][string]$Architecture,
        [Parameter(Mandatory)][string]$SourceRevision,
        [Parameter(Mandatory)][string]$Mode,
        [Parameter(Mandatory)][string]$ExpectedMessage,
        [string]$EngineRevision = $SourceRevision
    )

    try {
        Invoke-PackageBuild `
            -EnginePath $EnginePath `
            -Architecture $Architecture `
            -SourceRevision $SourceRevision `
            -Mode $Mode `
            -EngineRevision $EngineRevision
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedMessage) {
            throw
        }
        return
    }
    throw "Expected fake engine mode '$Mode' to be rejected."
}

try {
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $fakeProjectRoot = Join-Path $fixtureRoot "fake-engine"
    $fakePublishRoot = Join-Path $fixtureRoot "fake-engine-publish"
    $fakeToolsRoot = Join-Path $fixtureRoot "fake-tools"
    foreach ($directory in @($fakeProjectRoot, $fakePublishRoot, $fakeToolsRoot)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $architecture = if (
        [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
        [System.Runtime.InteropServices.Architecture]::Arm64) {
        "arm64"
    }
    else {
        "x64"
    }
    $runtimeIdentifier = "win-$architecture"
    $fakeProject = Join-Path $fakeProjectRoot "AgentDesk.FakeEngine.csproj"
    Write-Utf8NoBom -Path $fakeProject -Content @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>AgentDesk.FakeEngine</AssemblyName>
  </PropertyGroup>
</Project>
'@
    Write-Utf8NoBom -Path (Join-Path $fakeProjectRoot "Program.cs") -Content @'
var mode = Environment.GetEnvironmentVariable("AGENTDESK_FAKE_ENGINE_MODE") ?? "match";
var revision = Environment.GetEnvironmentVariable("AGENTDESK_FAKE_ENGINE_REVISION") ?? "0000000";
if (args.Length != 1 || args[0] != "--version")
{
    return 64;
}
switch (mode)
{
    case "failure":
        Console.WriteLine($"grok fake ({revision})");
        return 23;
    case "timeout":
        Thread.Sleep(TimeSpan.FromSeconds(30));
        Console.WriteLine($"grok fake ({revision})");
        return 0;
    case "oversize":
        Console.Write(new string('x', 65536));
        return 0;
    default:
        Console.WriteLine($"grok fake ({revision})");
        return 0;
}
'@

    & dotnet publish $fakeProject `
        --configuration Release `
        --runtime $runtimeIdentifier `
        --self-contained false `
        --output $fakePublishRoot `
        -p:PublishSingleFile=true `
        -p:DebugSymbols=false `
        -p:DebugType=None | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to build the fake engine fixture."
    }
    $fakeEngine = Join-Path $fakePublishRoot "AgentDesk.FakeEngine.exe"
    Set-PeStackReserve -Path $fakeEngine -StackReserveBytes ([uint64](8 * 1024 * 1024))

    $fakeDotnet = Join-Path $fakeToolsRoot "dotnet.cmd"
    Write-Utf8NoBom -Path $fakeDotnet -Content (@'
@echo off
setlocal EnableExtensions
set "output="
:parse
if "%~1"=="" goto publish
if /I "%~1"=="--output" (
  set "output=%~2"
  shift
)
shift
goto parse
:publish
if not defined output exit /b 90
if not exist "%output%" mkdir "%output%" || exit /b 91
type nul > "%output%\AgentDesk.Updater.exe"
exit /b 0
'@ -replace "`n", "`r`n")
    $env:PATH = "$fakeToolsRoot;$previousPath"

    $sourceRevision = "b26784f9c250398bc4d447613bcb0cf5a8cc8188"
    Invoke-PackageBuild `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision `
        -Mode "match"
    Invoke-PackageBuild `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision.Substring(0, 7) `
        -Mode "match"

    Assert-PackageRejectsEngine `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision `
        -Mode "stale" `
        -ExpectedMessage "revision" `
        -EngineRevision "04c549a00000"
    Assert-PackageRejectsEngine `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision `
        -Mode "collision" `
        -ExpectedMessage "revision" `
        -EngineRevision ($sourceRevision.Substring(0, 7) + "fffff")
    Assert-PackageRejectsEngine `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision `
        -Mode "failure" `
        -ExpectedMessage "code"
    Assert-PackageRejectsEngine `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision `
        -Mode "timeout" `
        -ExpectedMessage "timed out"
    Assert-PackageRejectsEngine `
        -EnginePath $fakeEngine `
        -Architecture $architecture `
        -SourceRevision $sourceRevision `
        -Mode "oversize" `
        -ExpectedMessage "output"

    & $buildScript `
        -Architecture $architecture `
        -Mode Portable `
        -Version "0.2.0-ci.7" `
        -NativeEnginePath (Join-Path $fixtureRoot "missing-engine.exe") `
        -OutputRoot (Join-Path $fixtureRoot "dry-run-output") `
        -SourceRevision $sourceRevision `
        -DryRun | Out-Null
}
finally {
    $env:PATH = $previousPath
    $env:AGENTDESK_FAKE_ENGINE_MODE = $previousMode
    $env:AGENTDESK_FAKE_ENGINE_REVISION = $previousRevision
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "AgentDesk engine revision tests passed."
