<#
.SYNOPSIS
Builds a Windows installer (Inno Setup) for AgentDesk Portable x64, or a
self-extracting zip fallback when Inno Setup is not installed.

.PARAMETER Version
Product version, e.g. 0.1.0-alpha.6

.PARAMETER PortableZipPath
Path to AgentDesk-<version>-win-x64-portable.zip

.PARAMETER OutputDirectory
Directory that will receive the Setup.exe (or install zip + README).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$PortableZipPath,

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$AppName = "AgentDesk",

    [string]$Publisher = "RkShadow Community"
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

$zipPath = Get-FullPath $PortableZipPath
$outRoot = Get-FullPath $OutputDirectory
if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Portable zip not found: $zipPath"
}
[System.IO.Directory]::CreateDirectory($outRoot) | Out-Null

$stageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("agentdesk-inno-" + [guid]::NewGuid().ToString("N"))
$payloadRoot = Join-Path $stageRoot "payload"
[System.IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $payloadRoot -Force

$appExe = Get-ChildItem -LiteralPath $payloadRoot -Filter "AgentDesk.App.exe" -Recurse -File |
    Select-Object -First 1
if ($null -eq $appExe) {
    throw "AgentDesk.App.exe was not found inside the portable zip."
}
# If the zip contains a single top-level folder, install from that folder.
$payloadEntries = Get-ChildItem -LiteralPath $payloadRoot
if ($payloadEntries.Count -eq 1 -and $payloadEntries[0].PSIsContainer) {
    $sourceDir = $payloadEntries[0].FullName
}
else {
    $sourceDir = $payloadRoot
}

$isccCandidates = @(
    "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:LocalAppData}\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

$setupName = "AgentDesk-$Version-win-x64-Setup.exe"
$setupPath = Join-Path $outRoot $setupName

if ($null -ne $iscc) {
    $issPath = Join-Path $stageRoot "AgentDesk.iss"
    # Inno Setup uses a simplified version; map prerelease to a 4-part file version.
    if ($Version -match '^(?<n>\d+\.\d+\.\d+)') {
        $numericVersion = $Matches['n'] + ".0"
    }
    else {
        $numericVersion = "0.1.0.0"
    }

    $sourceDirEscaped = $sourceDir -replace '\\', '\\'
    $outRootEscaped = $outRoot -replace '\\', '\\'

    $iconCandidates = @(
        (Join-Path $sourceDir "AgentDesk.ico"),
        (Join-Path $sourceDir "Assets\AgentDesk.ico"),
        (Join-Path $PSScriptRoot "..\..\desktop\src\AgentDesk.App\Assets\AgentDesk.ico")
    )
    $setupIcon = $iconCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $setupIconEscaped = if ($setupIcon) { ($setupIcon -replace '\\', '\\') } else { "" }
    $setupIconLine = if ($setupIconEscaped) { "SetupIconFile=$setupIconEscaped" } else { "" }
    $uninstallIcon = if ($setupIcon) { "{app}\AgentDesk.ico" } else { "{app}\{#MyAppExeName}" }

    $iss = @"
#define MyAppName "$AppName"
#define MyAppVersion "$Version"
#define MyAppPublisher "$Publisher"
#define MyAppURL "https://update.rkshadow.com/install/"
#define MyAppExeName "AgentDesk.App.exe"

[Setup]
AppId={{B7E3C2A1-9F40-4D6E-8C11-A6E0D35C0A60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={localappdata}\AgentDesk
DefaultGroupName=AgentDesk
DisableProgramGroupPage=yes
OutputDir=$outRootEscaped
OutputBaseFilename=AgentDesk-$Version-win-x64-Setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon=$uninstallIcon
VersionInfoVersion=$numericVersion
SetupLogging=yes
$setupIconLine

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[Files]
Source: "$sourceDirEscaped\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AgentDesk.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\AgentDesk.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 AgentDesk"; Flags: nowait postinstall skipifsilent
"@
    # Prefer English-only if Chinese language file is missing on the builder.
    $chineseIslCandidates = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\Languages\ChineseSimplified.isl",
        "${env:ProgramFiles(x86)}\Inno Setup 6\Languages\ChineseSimplified.isl",
        "${env:ProgramFiles}\Inno Setup 6\Languages\ChineseSimplified.isl",
        "${env:LocalAppData}\Programs\Inno Setup 7\Languages\ChineseSimplified.isl",
        "${env:ProgramFiles(x86)}\Inno Setup 7\Languages\ChineseSimplified.isl",
        "${env:ProgramFiles}\Inno Setup 7\Languages\ChineseSimplified.isl"
    )
    if (-not ($chineseIslCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)) {
        $iss = $iss -replace 'Name: "chinesesimplified"; MessagesFile: "compiler:Languages\\ChineseSimplified.isl"\r?\n', ''
    }

    [System.IO.File]::WriteAllText($issPath, $iss, [System.Text.UTF8Encoding]::new($true))
    Write-Host "Compiling Inno Setup installer with $iscc ..."
    & $iscc $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE"
    }
    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Inno Setup completed without producing $setupPath"
    }
}
else {
    Write-Warning "Inno Setup (ISCC.exe) not found. Installing via winget if possible..."
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -ne $winget) {
        winget install --id JRSoftware.InnoSetup --exact --silent --accept-package-agreements --accept-source-agreements
        $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }

    if ($null -ne $iscc) {
        # Re-enter this script after install.
        & $PSCommandPath -Version $Version -PortableZipPath $zipPath -OutputDirectory $outRoot
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
        return
    }

    Write-Warning "Falling back to portable install zip (no Inno Setup)."
    $fallbackZip = Join-Path $outRoot "AgentDesk-$Version-win-x64-install.zip"
    if (Test-Path -LiteralPath $fallbackZip) {
        Remove-Item -LiteralPath $fallbackZip -Force
    }
    Compress-Archive -Path (Join-Path $sourceDir '*') -DestinationPath $fallbackZip -Force
    $readme = @"
AgentDesk $Version Windows 安装包（Portable 分发）

本机未安装 Inno Setup，因此提供解压即用的 zip：

1. 解压到 %LOCALAPPDATA%\AgentDesk
2. 运行 AgentDesk.App.exe
3. 可选：将快捷方式固定到任务栏
4. 设置中开启检查更新后，会从 https://update.rkshadow.com/feed 获取签名更新

正式 Setup.exe 可在安装 Inno Setup 6 后重新运行 Build-AgentDeskWindowsInstaller.ps1 生成。
"@
    [System.IO.File]::WriteAllText(
        (Join-Path $outRoot "README-安装说明.txt"),
        $readme,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote fallback install zip: $fallbackZip"
    Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    return
}

$readme = @"
AgentDesk $Version Windows 安装程序

文件：$setupName

安装说明：
1. 双击运行 Setup（当前安装包为 development 构建，Windows SmartScreen 可能提示未知发布者）。
2. 默认安装到 %LOCALAPPDATA%\AgentDesk（无需管理员权限）。
3. 安装完成后启动 AgentDesk.App.exe。
4. 在设置中开启“检查更新”，客户端将从 https://update.rkshadow.com/feed 拉取 ECDSA 签名清单。

更新服务器：https://update.rkshadow.com/
"@
[System.IO.File]::WriteAllText(
    (Join-Path $outRoot "README-安装说明.txt"),
    $readme,
    [System.Text.UTF8Encoding]::new($false))

Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Installer ready: $setupPath"
