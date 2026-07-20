<#
.SYNOPSIS
Generates deterministic third-party notices for the AgentDesk desktop application.

.DESCRIPTION
Reads the committed npm lockfile and installed production packages, validates
their license metadata and license files, and resolves the complete restored
.NET package/runtime closure from project.assets.json. URL-only NuGet licenses
must be backed by repository-pinned legal text, and optional published .deps.json
files are checked against the resolved notice closure.
#>
[CmdletBinding()]
param(
    [string]$LockFile,
    [string]$AppProject,
    [string]$AssetsFile,
    [string]$LicenseManifest,
    [string[]]$PublishedDepsFile = @(),
    [string]$OutputPath,
    [string]$NodePath = "node"
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

$repositoryRoot = Get-FullPath (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($LockFile)) {
    $LockFile = Join-Path $repositoryRoot "desktop\web\package-lock.json"
}
if ([string]::IsNullOrWhiteSpace($AppProject)) {
    $AppProject = Join-Path $repositoryRoot "desktop\src\AgentDesk.App\AgentDesk.App.csproj"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot "desktop\THIRD-PARTY-NOTICES.md"
}
if ([string]::IsNullOrWhiteSpace($LicenseManifest)) {
    $LicenseManifest = Join-Path $repositoryRoot "desktop\third-party-licenses\manifest.json"
}
if ([string]::IsNullOrWhiteSpace($AssetsFile)) {
    $AssetsFile = Join-Path (Split-Path -Parent $AppProject) "obj\project.assets.json"
}

$LockFile = Get-FullPath $LockFile
$AppProject = Get-FullPath $AppProject
$AssetsFile = Get-FullPath $AssetsFile
$LicenseManifest = Get-FullPath $LicenseManifest
$OutputPath = Get-FullPath $OutputPath
$resolvedPublishedDepsFiles = @(
    foreach ($depsFile in $PublishedDepsFile) {
        $resolvedDepsFile = Get-FullPath $depsFile
        if (-not (Test-Path -LiteralPath $resolvedDepsFile -PathType Leaf)) {
            throw "Published dependency graph does not exist: $resolvedDepsFile"
        }
        $resolvedDepsFile
    }
)
foreach ($requiredInput in @($LockFile, $AppProject, $AssetsFile)) {
    if (-not (Test-Path -LiteralPath $requiredInput -PathType Leaf)) {
        throw "Required notice input does not exist: $requiredInput"
    }
}

$pinnedLegalEntries = [System.Collections.Generic.Dictionary[string,object]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
if (Test-Path -LiteralPath $LicenseManifest -PathType Leaf) {
    $licenseManifestData = Get-Content -LiteralPath $LicenseManifest -Raw | ConvertFrom-Json
    if ([int]$licenseManifestData.version -ne 1 -or $null -eq $licenseManifestData.entries) {
        throw "The repository-pinned legal text manifest must use version 1 and declare entries: $LicenseManifest"
    }
    $licenseManifestRoot = Split-Path -Parent $LicenseManifest
    foreach ($entry in @($licenseManifestData.entries)) {
        $packageId = [string]$entry.packageId
        $packageVersion = [string]$entry.packageVersion
        $licenseUrl = [string]$entry.licenseUrl
        $relativePath = [string]$entry.path
        $sha256 = ([string]$entry.sha256).ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($packageId) -or
            [string]::IsNullOrWhiteSpace($packageVersion) -or
            [string]::IsNullOrWhiteSpace($licenseUrl) -or
            [string]::IsNullOrWhiteSpace($relativePath) -or
            $sha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Repository-pinned legal text manifest entry is incomplete or invalid: $($entry | ConvertTo-Json -Compress)"
        }
        if ([System.IO.Path]::IsPathRooted($relativePath)) {
            throw "Repository-pinned legal text path must be relative: $relativePath"
        }
        $legalTextPath = Get-FullPath (Join-Path $licenseManifestRoot $relativePath)
        $manifestPrefix = $licenseManifestRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $legalTextPath.StartsWith($manifestPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Repository-pinned legal text path escapes its manifest directory: $relativePath"
        }
        if (-not (Test-Path -LiteralPath $legalTextPath -PathType Leaf)) {
            throw "Repository-pinned legal text does not exist: $legalTextPath"
        }
        $actualHash = (Get-FileHash -LiteralPath $legalTextPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not [string]::Equals($actualHash, $sha256, [System.StringComparison]::Ordinal)) {
            throw "Repository-pinned legal text SHA-256 mismatch for $packageId@${packageVersion}: expected $sha256, found $actualHash."
        }
        $key = "$packageId/$packageVersion"
        if ($pinnedLegalEntries.ContainsKey($key)) {
            throw "Repository-pinned legal text manifest contains a duplicate package entry: $key"
        }
        $pinnedLegalEntries[$key] = [pscustomobject]@{
            PackageId = $packageId
            PackageVersion = $packageVersion
            LicenseUrl = $licenseUrl
            RelativePath = $relativePath.Replace('\', '/')
            Path = $legalTextPath
            Sha256 = $sha256
        }
    }
}

$nodeProgram = @'
const fs = require('fs');
const path = require('path');

const lockFile = path.resolve(process.argv[1]);
const webRoot = path.dirname(lockFile);
const nodeModulesRoot = path.resolve(webRoot, 'node_modules');
const lock = JSON.parse(fs.readFileSync(lockFile, 'utf8'));
if (lock.lockfileVersion !== 3 || !lock.packages || !lock.packages['']) {
  throw new Error('Expected an npm lockfileVersion 3 packages map.');
}

function packageNameFromPath(lockPath) {
  return lockPath.split('node_modules/').pop();
}

function repositoryUrl(value) {
  const raw = typeof value === 'string' ? value : value && value.url;
  if (!raw) return null;
  return raw.replace(/^git\+/, '').replace(/^git:\/\//, 'https://').replace(/\.git$/, '');
}

const records = [];
for (const [lockPath, metadata] of Object.entries(lock.packages)) {
  if (!lockPath.startsWith('node_modules/') || metadata.dev === true) continue;

  const packageDirectory = path.resolve(webRoot, lockPath);
  if (!packageDirectory.startsWith(nodeModulesRoot + path.sep)) {
    throw new Error(`Package path escapes node_modules: ${lockPath}`);
  }
  if (!fs.existsSync(packageDirectory)) {
    throw new Error(`Production package is not installed: ${lockPath}`);
  }

  const name = packageNameFromPath(lockPath);
  if (!metadata.version) {
    throw new Error(`Production package has no version metadata: ${name}`);
  }
  if (typeof metadata.license !== 'string' || !metadata.license.trim()) {
    throw new Error(`Production package has no identifiable license metadata: ${name}@${metadata.version}`);
  }

  const licenseFiles = fs.readdirSync(packageDirectory, { withFileTypes: true })
    .filter((entry) => entry.isFile() && /^(licen[cs]e|copying|notice|third[-_ ]?party[-_ ]?notices?)(\..*)?$/i.test(entry.name))
    .map((entry) => entry.name)
    .sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
  if (licenseFiles.length === 0) {
    throw new Error(`Production package has no identifiable license file: ${name}@${metadata.version}`);
  }

  const packageJsonPath = path.join(packageDirectory, 'package.json');
  const packageJson = fs.existsSync(packageJsonPath)
    ? JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'))
    : {};
  records.push({
    name,
    version: metadata.version,
    license: metadata.license.trim(),
    lockPath,
    licenseFiles,
    source: packageJson.homepage || repositoryUrl(packageJson.repository) || null
  });
}

const directDependencies = Object.keys(lock.packages[''].dependencies || {});
for (const name of directDependencies) {
  if (!records.some((record) => record.name === name)) {
    throw new Error(`Direct production dependency was not resolved from the lockfile: ${name}`);
  }
}

records.sort((left, right) => {
  for (const key of ['name', 'version', 'lockPath']) {
    if (left[key] < right[key]) return -1;
    if (left[key] > right[key]) return 1;
  }
  return 0;
});
process.stdout.write(JSON.stringify(records));
'@

$nodeOutput = & $NodePath -e $nodeProgram $LockFile
if ($LASTEXITCODE -ne 0) {
    throw "$NodePath failed to resolve production npm licenses (exit code $LASTEXITCODE)."
}
$parsedNpmPackages = ($nodeOutput -join "`n") | ConvertFrom-Json
$npmPackages = @()
foreach ($package in $parsedNpmPackages) {
    $npmPackages += $package
}
if ($npmPackages.Count -eq 0) {
    throw "The npm lockfile did not resolve any production packages."
}

function Get-ExactPackageVersion {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$VersionRange
    )

    $trimmed = $VersionRange.Trim()
    if ($trimmed -notmatch '^\[([^,\]]+),\s*([^\]]+)\]$' -or
        -not [string]::Equals($Matches[1].Trim(), $Matches[2].Trim(), [System.StringComparison]::Ordinal)) {
        throw "Restored download dependency $PackageId does not use an exact version: $VersionRange"
    }
    return $Matches[1].Trim()
}

function Get-PackageDirectory {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string[]]$PackageFolders,
        [switch]$PreferPackageMetadata
    )

    $fallback = $null
    foreach ($packageFolder in $PackageFolders) {
        $fullRoot = (Get-FullPath $packageFolder).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $candidate = Get-FullPath (Join-Path $fullRoot $PackagePath)
        $requiredPrefix = $fullRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $candidate.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "NuGet package path escapes the restored package root: $PackagePath"
        }
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            if (-not $PreferPackageMetadata) {
                return $candidate
            }
            if ($null -eq $fallback) {
                $fallback = $candidate
            }
            if (@(Get-ChildItem -LiteralPath $candidate -File -Filter "*.nuspec").Count -gt 0) {
                return $candidate
            }
        }
    }
    if ($null -ne $fallback) {
        return $fallback
    }
    throw "Restored NuGet package is missing from all package folders: $PackagePath"
}

function Get-NuGetPackageRecord {
    param(
        [Parameter(Mandatory)][string]$ExpectedId,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$Role
    )

    $nuspecs = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Filter "*.nuspec")
    if ($nuspecs.Count -ne 1) {
        throw "NuGet package $ExpectedId@$ExpectedVersion must contain exactly one top-level .nuspec file."
    }
    [xml]$nuspec = Get-Content -LiteralPath $nuspecs[0].FullName -Raw
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) {
        throw "NuGet package $ExpectedId@$ExpectedVersion has no metadata element."
    }

    $idNode = $metadata.SelectSingleNode("*[local-name()='id']")
    $versionNode = $metadata.SelectSingleNode("*[local-name()='version']")
    $actualId = if ($null -eq $idNode) { "" } else { $idNode.InnerText.Trim() }
    $actualVersion = if ($null -eq $versionNode) { "" } else { $versionNode.InnerText.Trim() }
    if (-not [string]::Equals($actualId, $ExpectedId, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($actualVersion, $ExpectedVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "NuGet package metadata mismatch: expected $ExpectedId@$ExpectedVersion, found $actualId@$actualVersion."
    }

    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $licenseUrlNode = $metadata.SelectSingleNode("*[local-name()='licenseUrl']")
    $licenseValue = if ($null -eq $licenseNode) { "" } else { $licenseNode.InnerText.Trim() }
    $licenseType = if ($null -eq $licenseNode -or $null -eq $licenseNode.Attributes["type"]) {
        ""
    }
    else {
        $licenseNode.Attributes["type"].Value.Trim()
    }
    $licenseUrl = if ($null -eq $licenseUrlNode) { "" } else { $licenseUrlNode.InnerText.Trim() }
    if ([string]::IsNullOrWhiteSpace($licenseValue) -and [string]::IsNullOrWhiteSpace($licenseUrl)) {
        throw "NuGet package $ExpectedId@$ExpectedVersion has no identifiable license metadata."
    }
    $pinnedLegalText = $null
    if ([string]::IsNullOrWhiteSpace($licenseValue)) {
        $pinnedKey = "$ExpectedId/$ExpectedVersion"
        if (-not $pinnedLegalEntries.ContainsKey($pinnedKey)) {
            throw "NuGet package $ExpectedId@$ExpectedVersion requires repository-pinned legal text for URL-only license metadata."
        }
        $pinnedLegalText = $pinnedLegalEntries[$pinnedKey]
        if (-not [string]::Equals(
                [string]$pinnedLegalText.LicenseUrl,
                $licenseUrl,
                [System.StringComparison]::Ordinal)) {
            throw "Repository-pinned legal text URL mismatch for $ExpectedId@$ExpectedVersion."
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($licenseType) -and
        $licenseType -notin @("file", "expression")) {
        throw "NuGet package $ExpectedId@$ExpectedVersion has unsupported license metadata type '$licenseType'."
    }

    $licensePaths = [System.Collections.Generic.Dictionary[string,string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    if ([string]::Equals($licenseType, "file", [System.StringComparison]::OrdinalIgnoreCase)) {
        if ([System.IO.Path]::IsPathRooted($licenseValue)) {
            throw "NuGet package $ExpectedId@$ExpectedVersion has an absolute license file path."
        }
        $explicitLicense = Get-FullPath (Join-Path $PackageDirectory $licenseValue)
        $packagePrefix = $PackageDirectory.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        if (-not $explicitLicense.StartsWith($packagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "NuGet package $ExpectedId@$ExpectedVersion has a license file outside its package directory."
        }
        if (-not (Test-Path -LiteralPath $explicitLicense -PathType Leaf)) {
            throw "NuGet package $ExpectedId@$ExpectedVersion is missing declared license file '$licenseValue'."
        }
        $licensePaths[$licenseValue.Replace('\', '/')] = $explicitLicense
    }
    if ($null -ne $pinnedLegalText) {
        $licensePaths["repository-pinned/$($pinnedLegalText.RelativePath)"] = $pinnedLegalText.Path
    }

    foreach ($candidate in @(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object {
        $_.Name -match '^(licen[cs]e|copying|notice|third[-_ ]?party[-_ ]?notices?)(\..*)?$'
    })) {
        $licensePaths[$candidate.Name] = $candidate.FullName
    }

    $licenseFiles = @(
        foreach ($entry in ($licensePaths.GetEnumerator() | Sort-Object Key)) {
            $licenseText = [System.IO.File]::ReadAllText($entry.Value, [System.Text.Encoding]::UTF8)
            if ([string]::IsNullOrWhiteSpace($licenseText)) {
                throw "NuGet package $ExpectedId@$ExpectedVersion has an empty license/notice file '$($entry.Key)'."
            }
            [pscustomobject]@{
                Name = $entry.Key
                Path = $entry.Value
            }
        }
    )

    $projectUrlNode = $metadata.SelectSingleNode("*[local-name()='projectUrl']")
    $repositoryNode = $metadata.SelectSingleNode("*[local-name()='repository']")
    $source = if ($null -ne $projectUrlNode -and -not [string]::IsNullOrWhiteSpace($projectUrlNode.InnerText)) {
        $projectUrlNode.InnerText.Trim()
    }
    elseif ($null -ne $repositoryNode -and $null -ne $repositoryNode.Attributes["url"]) {
        $repositoryNode.Attributes["url"].Value.Trim()
    }
    else {
        ""
    }
    $developmentNode = $metadata.SelectSingleNode("*[local-name()='developmentDependency']")
    $isDevelopmentDependency = $null -ne $developmentNode -and
        [string]::Equals($developmentNode.InnerText.Trim(), "true", [System.StringComparison]::OrdinalIgnoreCase)

    return [pscustomobject]@{
        Id = $ExpectedId
        Version = $ExpectedVersion
        Role = if ($isDevelopmentDependency) { "Build-only dependency (not redistributed)." } else { $Role }
        Source = $source
        LicenseType = $licenseType
        LicenseValue = $licenseValue
        LicenseUrl = $licenseUrl
        PinnedLegalText = $pinnedLegalText
        LicenseFiles = $licenseFiles
    }
}

function Get-DotNetSdkHostPackRecord {
    param(
        [Parameter(Mandatory)][string]$ExpectedId,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$Role
    )

    if ($ExpectedId -notmatch '^Microsoft\.NETCore\.App\.Host\.(win-(?:x64|arm64))$') {
        throw "Only .NET Host packs can be resolved from DOTNET_ROOT packs: $ExpectedId"
    }
    $runtimeId = $Matches[1]
    $versionDirectory = Get-FullPath $PackageDirectory
    $packageIdDirectory = Split-Path -Parent $versionDirectory
    $packsDirectory = Split-Path -Parent $packageIdDirectory
    $dotnetRoot = Split-Path -Parent $packsDirectory
    if (-not [string]::Equals(
            (Split-Path -Leaf $versionDirectory),
            $ExpectedVersion,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            (Split-Path -Leaf $packageIdDirectory),
            $ExpectedId,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            (Split-Path -Leaf $packsDirectory),
            "packs",
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ".NET SDK Host pack path does not match $ExpectedId@${ExpectedVersion}: $PackageDirectory"
    }
    $appHostPath = Join-Path $versionDirectory "runtimes\$runtimeId\native\apphost.exe"
    if (-not (Test-Path -LiteralPath $appHostPath -PathType Leaf)) {
        throw ".NET SDK Host pack $ExpectedId@$ExpectedVersion is missing $runtimeId apphost.exe."
    }

    $licenseFiles = @(
        foreach ($legalFileName in @("LICENSE.txt", "ThirdPartyNotices.txt")) {
            $legalFilePath = Join-Path $dotnetRoot $legalFileName
            if (-not (Test-Path -LiteralPath $legalFilePath -PathType Leaf)) {
                throw ".NET SDK Host pack $ExpectedId@$ExpectedVersion is missing DOTNET_ROOT legal file '$legalFileName'."
            }
            $legalText = [System.IO.File]::ReadAllText($legalFilePath, [System.Text.Encoding]::UTF8)
            if ([string]::IsNullOrWhiteSpace($legalText)) {
                throw ".NET SDK Host pack $ExpectedId@$ExpectedVersion has an empty DOTNET_ROOT legal file '$legalFileName'."
            }
            [pscustomobject]@{
                Name = "DOTNET_ROOT/$legalFileName"
                Path = $legalFilePath
            }
        }
    )

    return [pscustomobject]@{
        Id = $ExpectedId
        Version = $ExpectedVersion
        Role = $Role
        Source = "https://dot.net/"
        LicenseType = "expression"
        LicenseValue = "MIT"
        LicenseUrl = "https://licenses.nuget.org/MIT"
        PinnedLegalText = $null
        LicenseFiles = $licenseFiles
    }
}

function Get-HostPackageRecord {
    param(
        [Parameter(Mandatory)][string]$ExpectedId,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$Role
    )

    $nuspecs = @(Get-ChildItem -LiteralPath $PackageDirectory -File -Filter "*.nuspec")
    if ($nuspecs.Count -eq 1) {
        return Get-NuGetPackageRecord `
            -ExpectedId $ExpectedId `
            -ExpectedVersion $ExpectedVersion `
            -PackageDirectory $PackageDirectory `
            -Role $Role
    }
    if ($nuspecs.Count -gt 1) {
        throw "NuGet package $ExpectedId@$ExpectedVersion must contain exactly one top-level .nuspec file."
    }
    return Get-DotNetSdkHostPackRecord `
        -ExpectedId $ExpectedId `
        -ExpectedVersion $ExpectedVersion `
        -PackageDirectory $PackageDirectory `
        -Role $Role
}

$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
if ($null -eq $assets.libraries -or $null -eq $assets.packageFolders -or $null -eq $assets.project) {
    throw "The NuGet assets file is missing required libraries, packageFolders, or project data: $AssetsFile"
}
$packageFolders = @($assets.packageFolders.psobject.Properties.Name | Sort-Object)
if ($packageFolders.Count -eq 0) {
    throw "The NuGet assets file does not declare any package folders: $AssetsFile"
}
$packageSearchRoots = [System.Collections.Generic.List[string]]::new()
foreach ($packageFolder in $packageFolders) {
    $packageSearchRoots.Add($packageFolder)
}
foreach ($framework in @($assets.project.frameworks.psobject.Properties)) {
    $ridGraphPath = [string]$framework.Value.runtimeIdentifierGraphPath
    if ([string]::IsNullOrWhiteSpace($ridGraphPath)) {
        continue
    }
    $sdkVersionDirectory = Split-Path -Parent (Get-FullPath $ridGraphPath)
    $sdkDirectory = Split-Path -Parent $sdkVersionDirectory
    $dotnetRoot = Split-Path -Parent $sdkDirectory
    $dotnetPacksRoot = Join-Path $dotnetRoot "packs"
    if ((Test-Path -LiteralPath $dotnetPacksRoot -PathType Container) -and
        -not $packageSearchRoots.Contains($dotnetPacksRoot)) {
        $packageSearchRoots.Add($dotnetPacksRoot)
    }
}

$resolvedPackages = [System.Collections.Generic.Dictionary[string,object]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($library in @($assets.libraries.psobject.Properties)) {
    if (-not [string]::Equals([string]$library.Value.type, "package", [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    $separator = $library.Name.LastIndexOf('/')
    if ($separator -le 0 -or $separator -eq $library.Name.Length - 1) {
        throw "NuGet assets library has an invalid package identity: $($library.Name)"
    }
    $packageId = $library.Name.Substring(0, $separator)
    $packageVersion = $library.Name.Substring($separator + 1)
    $packagePath = [string]$library.Value.path
    if ([string]::IsNullOrWhiteSpace($packagePath)) {
        $packagePath = "$($packageId.ToLowerInvariant())/$packageVersion"
    }
    $packageDirectory = Get-PackageDirectory -PackagePath $packagePath -PackageFolders $packageSearchRoots.ToArray()
    $resolvedPackages[$library.Name] = Get-NuGetPackageRecord `
        -ExpectedId $packageId `
        -ExpectedVersion $packageVersion `
        -PackageDirectory $packageDirectory `
        -Role "Restored desktop dependency; runtime/content assets may be redistributed with AgentDesk."
}

$downloadDependencies = [System.Collections.Generic.Dictionary[string,string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
foreach ($framework in @($assets.project.frameworks.psobject.Properties)) {
    foreach ($dependency in @($framework.Value.downloadDependencies)) {
        if ($null -eq $dependency -or [string]::IsNullOrWhiteSpace([string]$dependency.name)) {
            continue
        }
        $packageId = [string]$dependency.name
        if ($packageId -notmatch '^Microsoft\.NETCore\.App\.(Runtime|Host)\.win-(x64|arm64)$' -and
            $packageId -ne "Microsoft.Windows.SDK.NET.Ref") {
            continue
        }
        $packageVersion = Get-ExactPackageVersion -PackageId $packageId -VersionRange ([string]$dependency.version)
        $downloadDependencies[$packageId] = $packageVersion
        $identity = "$packageId/$packageVersion"
        if (-not $resolvedPackages.ContainsKey($identity)) {
            $packagePath = "$($packageId.ToLowerInvariant())/$packageVersion"
            $isHostPackage = $packageId -match '^Microsoft\.NETCore\.App\.Host\.'
            $packageDirectory = Get-PackageDirectory `
                -PackagePath $packagePath `
                -PackageFolders $packageSearchRoots.ToArray() `
                -PreferPackageMetadata:$isHostPackage
            if ($isHostPackage) {
                $resolvedPackages[$identity] = Get-HostPackageRecord `
                    -ExpectedId $packageId `
                    -ExpectedVersion $packageVersion `
                    -PackageDirectory $packageDirectory `
                    -Role "Self-contained .NET application host redistributed with AgentDesk."
            }
            else {
                $resolvedPackages[$identity] = Get-NuGetPackageRecord `
                    -ExpectedId $packageId `
                    -ExpectedVersion $packageVersion `
                    -PackageDirectory $packageDirectory `
                    -Role "Self-contained .NET/Windows runtime dependency redistributed with AgentDesk."
            }
        }
    }
}

foreach ($runtimeDependency in @($downloadDependencies.GetEnumerator() | Where-Object {
    $_.Key -match '^Microsoft\.NETCore\.App\.Runtime\.(win-(?:x64|arm64))$'
})) {
    $runtimeId = [System.Text.RegularExpressions.Regex]::Match(
        $runtimeDependency.Key,
        '^Microsoft\.NETCore\.App\.Runtime\.(win-(?:x64|arm64))$').Groups[1].Value
    $hostPackageId = "Microsoft.NETCore.App.Host.$runtimeId"
    $hostVersion = $runtimeDependency.Value
    $hostIdentity = "$hostPackageId/$hostVersion"
    if ($resolvedPackages.ContainsKey($hostIdentity)) {
        continue
    }
    $hostPackagePath = "$($hostPackageId.ToLowerInvariant())/$hostVersion"
    $hostPackageDirectory = Get-PackageDirectory `
        -PackagePath $hostPackagePath `
        -PackageFolders $packageSearchRoots.ToArray() `
        -PreferPackageMetadata
    $resolvedPackages[$hostIdentity] = Get-HostPackageRecord `
        -ExpectedId $hostPackageId `
        -ExpectedVersion $hostVersion `
        -PackageDirectory $hostPackageDirectory `
        -Role "Self-contained .NET application host redistributed with AgentDesk."
}

$dotnetPackages = @($resolvedPackages.Values | Sort-Object `
    @{ Expression = { $_.Id.ToLowerInvariant() } }, `
    @{ Expression = { $_.Version } })
if ($dotnetPackages.Count -eq 0) {
    throw "The NuGet assets file did not resolve any desktop packages."
}

foreach ($publishedDepsPath in $resolvedPublishedDepsFiles) {
    $publishedDeps = Get-Content -LiteralPath $publishedDepsPath -Raw | ConvertFrom-Json
    if ($null -eq $publishedDeps.libraries) {
        throw "Published dependency graph has no libraries map: $publishedDepsPath"
    }
    $publishedPackageCount = 0
    foreach ($publishedLibrary in @($publishedDeps.libraries.psobject.Properties)) {
        if (-not [string]::Equals(
                [string]$publishedLibrary.Value.type,
                "package",
                [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $publishedPackageCount++
        $publishedIdentity = $publishedLibrary.Name -replace '^runtimepack\.', ''
        if (-not $resolvedPackages.ContainsKey($publishedIdentity)) {
            throw "Published dependency is not covered by the generated notice closure: $publishedIdentity ($publishedDepsPath)"
        }
    }
    if ($publishedPackageCount -eq 0) {
        throw "Published dependency graph does not contain any package libraries: $publishedDepsPath"
    }
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add("# AgentDesk desktop third-party notices")
$lines.Add("")
$lines.Add("This file is generated by ``scripts/agentdesk/Generate-AgentDeskThirdPartyNotices.ps1`` from")
$lines.Add("the committed npm lockfile, restored ``project.assets.json``, repository-pinned legal text,")
$lines.Add("and installed package legal files. Final publish dependency graphs are checked in release CI.")
$lines.Add("Do not edit it by hand. Regenerate it after changing production dependencies.")
$lines.Add("")
$lines.Add("## Restored .NET desktop dependencies")
$lines.Add("")
foreach ($package in $dotnetPackages) {
    $nugetUrl = "https://www.nuget.org/packages/$($package.Id)/$($package.Version)"
    $lines.Add("### $($package.Id) $($package.Version)")
    $lines.Add("")
    $lines.Add("- Role: $($package.Role)")
    $lines.Add("- Package: $nugetUrl")
    if (-not [string]::IsNullOrWhiteSpace([string]$package.Source)) {
        $lines.Add("- Source / project: $($package.Source)")
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$package.LicenseValue)) {
        $metadataLabel = if ([string]::IsNullOrWhiteSpace([string]$package.LicenseType)) {
            "declared value"
        }
        else {
            [string]$package.LicenseType
        }
        $lines.Add("- License metadata ($metadataLabel): ``$($package.LicenseValue)``")
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$package.LicenseUrl)) {
        $lines.Add("- License URL: $($package.LicenseUrl)")
    }
    if ($null -ne $package.PinnedLegalText) {
        $lines.Add("- Repository-pinned legal text: ``$($package.PinnedLegalText.RelativePath)``")
        $lines.Add("- Repository-pinned source URL: $($package.PinnedLegalText.LicenseUrl)")
        $lines.Add("- Repository-pinned SHA-256: ``$($package.PinnedLegalText.Sha256)``")
    }
    $lines.Add("")

    foreach ($licenseFile in @($package.LicenseFiles)) {
        $licenseText = [System.IO.File]::ReadAllText($licenseFile.Path, [System.Text.Encoding]::UTF8)
        $lines.Add("#### $($licenseFile.Name)")
        $lines.Add("")
        foreach ($licenseLine in (($licenseText.TrimEnd() -replace "\r\n?", "`n") -split "`n")) {
            $normalizedLicenseLine = $licenseLine.TrimEnd()
            if ($normalizedLicenseLine.Length -eq 0) {
                $lines.Add("")
            }
            else {
                $lines.Add("    $normalizedLicenseLine")
            }
        }
        $lines.Add("")
    }
}

$lines.Add("## Production npm dependencies")
$lines.Add("")
$lines.Add("The following entries are every non-development package recorded in")
$lines.Add("``desktop/web/package-lock.json``. License texts are copied from the corresponding")
$lines.Add("installed package directories; package metadata is not used as a substitute for the text.")
$lines.Add("")
foreach ($package in $npmPackages) {
    $encodedName = [System.Uri]::EscapeDataString([string]$package.name)
    $packageUrl = "https://www.npmjs.com/package/$encodedName/v/$($package.version)"
    $lines.Add("### ``$($package.name)`` $($package.version)")
    $lines.Add("")
    $lines.Add("- Package: $packageUrl")
    if (-not [string]::IsNullOrWhiteSpace([string]$package.source)) {
        $lines.Add("- Source / project: $($package.source)")
    }
    $lines.Add("- License metadata: ``$($package.license)``")
    $lines.Add("")

    foreach ($licenseFile in @($package.licenseFiles)) {
        $licensePath = Join-Path (Join-Path (Split-Path -Parent $LockFile) ([string]$package.lockPath)) ([string]$licenseFile)
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "Resolved license file disappeared while generating notices: $licensePath"
        }
        $licenseText = [System.IO.File]::ReadAllText($licensePath, [System.Text.Encoding]::UTF8)
        if ([string]::IsNullOrWhiteSpace($licenseText)) {
            throw "Production package has an empty license file: $licensePath"
        }
        $lines.Add("#### $licenseFile")
        $lines.Add("")
        foreach ($licenseLine in (($licenseText.TrimEnd() -replace "\r\n?", "`n") -split "`n")) {
            $normalizedLicenseLine = $licenseLine.TrimEnd()
            if ($normalizedLicenseLine.Length -eq 0) {
                $lines.Add("")
            }
            else {
                $lines.Add("    $normalizedLicenseLine")
            }
        }
        $lines.Add("")
    }
}

$normalizedOutputLines = foreach ($line in (($lines -join "`n") -split "`n")) {
    $line -replace '[\x20\x09]+$', ''
}
$content = ($normalizedOutputLines -join "`n").TrimEnd() + "`n"
$outputDirectory = Split-Path -Parent $OutputPath
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[System.IO.File]::WriteAllText($OutputPath, $content, [System.Text.UTF8Encoding]::new($false))
Write-Output $OutputPath
