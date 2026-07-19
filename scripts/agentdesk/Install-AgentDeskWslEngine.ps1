<#
.SYNOPSIS
Installs the bundled Linux sidecar into a WSL distribution.

.DESCRIPTION
Converts the bundled sidecar path with wslpath, then uses sudo install to place
it at /usr/local/bin/agentdesk-engine. AgentDesk WslStrict executes that fixed
installed path, so this installation step is required.

.PARAMETER SourcePath
Optional Windows path to the Linux sidecar. Defaults to wsl/agentdesk-engine
next to this script.

.PARAMETER DistributionName
Optional installed WSL distribution. When omitted, AGENTDESK_WSL_DISTRIBUTION
is used; otherwise exactly one installed non-Docker distribution must exist.
#>
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot "wsl\agentdesk-engine"),
    [string]$DistributionName = $env:AGENTDESK_WSL_DISTRIBUTION,
    [Parameter(DontShow)]
    [scriptblock]$WslCommandRunner
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($null -eq $WslCommandRunner -and
    -not (Get-Command "wsl.exe" -ErrorAction SilentlyContinue)) {
    throw "WSL is not installed or wsl.exe is unavailable."
}
$SourcePath = [System.IO.Path]::GetFullPath($SourcePath)
if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
    throw "The bundled WSL sidecar was not found: $SourcePath"
}

function Invoke-AgentDeskWsl {
    param([Parameter(Mandatory)][string[]]$Arguments)

    if ($null -ne $WslCommandRunner) {
        $result = & $WslCommandRunner $Arguments
        if ($null -eq $result -or
            $null -eq $result.PSObject.Properties["ExitCode"] -or
            $null -eq $result.PSObject.Properties["Output"]) {
            throw "The WSL command runner returned an invalid result."
        }
        return $result
    }

    $output = @(& wsl.exe @Arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

function Get-NormalizedWslOutput {
    param([Parameter(Mandatory)][object[]]$Output)

    return @($Output |
        Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } |
        ForEach-Object {
            ([string]$_).Replace(([char]0).ToString(), [string]::Empty).Trim()
        } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

$listResult = Invoke-AgentDeskWsl -Arguments @("--list", "--quiet")
if ($listResult.ExitCode -ne 0) {
    throw "WSL distributions could not be enumerated."
}
$installedDistributions = @(Get-NormalizedWslOutput -Output @($listResult.Output))
$eligibleDistributions = @($installedDistributions |
    Where-Object { $_ -notmatch "^(?i:docker-desktop)" } |
    Sort-Object -Unique)
if (-not [string]::IsNullOrWhiteSpace($DistributionName)) {
    $requestedDistribution = $DistributionName.Trim()
    if ($requestedDistribution -match "[\x00-\x1f\x7f]" -or
        $requestedDistribution -match "^(?i:docker-desktop)") {
        throw "The configured WSL distribution is not eligible for AgentDesk."
    }
    $selectedDistribution = @($eligibleDistributions |
        Where-Object { $_ -ieq $requestedDistribution } |
        Select-Object -First 1)
    if ($selectedDistribution.Count -ne 1) {
        throw "The configured WSL distribution is not installed: $requestedDistribution"
    }
    $selectedDistribution = $selectedDistribution[0]
}
elseif ($eligibleDistributions.Count -eq 1) {
    $selectedDistribution = $eligibleDistributions[0]
}
else {
    throw "AgentDesk requires exactly one installed non-Docker WSL distribution or an explicit -DistributionName."
}

function Invoke-SelectedAgentDeskWsl {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $selectedArguments = @("--distribution", $selectedDistribution) + $Arguments
    return Invoke-AgentDeskWsl -Arguments $selectedArguments
}

$convertedPathResult = Invoke-SelectedAgentDeskWsl -Arguments @(
    "--exec",
    "wslpath",
    "-a",
    $SourcePath
)
if ($convertedPathResult.ExitCode -ne 0) {
    throw "wslpath could not convert the sidecar path for $selectedDistribution."
}
$convertedPathOutput = @(Get-NormalizedWslOutput -Output @($convertedPathResult.Output))
$wslSourcePath = $convertedPathOutput | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($wslSourcePath)) {
    throw "wslpath returned an empty sidecar path."
}

Write-Host "Installing AgentDesk WSL engine into $selectedDistribution"
$userResult = Invoke-SelectedAgentDeskWsl -Arguments @("--exec", "id", "-u")
if ($userResult.ExitCode -ne 0) {
    throw "The WSL distribution user identity could not be determined."
}
$userOutput = @(Get-NormalizedWslOutput -Output @($userResult.Output))
$installCommand = if ($userOutput.Count -gt 0 -and $userOutput[0] -eq "0") {
    @("--exec", "install", "-m", "0755", "--", $wslSourcePath, "/usr/local/bin/agentdesk-engine")
}
else {
    @("--exec", "sudo", "install", "-m", "0755", "--", $wslSourcePath, "/usr/local/bin/agentdesk-engine")
}
$installResult = Invoke-SelectedAgentDeskWsl -Arguments $installCommand
if ($installResult.ExitCode -ne 0) {
    throw "The WSL sidecar installation failed."
}

$testResult = Invoke-SelectedAgentDeskWsl -Arguments @(
    "--exec",
    "test",
    "-x",
    "/usr/local/bin/agentdesk-engine"
)
if ($testResult.ExitCode -ne 0) {
    throw "The installed WSL sidecar is not executable."
}

$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $SourcePath).Hash.ToLowerInvariant()
$hashResult = Invoke-SelectedAgentDeskWsl -Arguments @(
    "--exec",
    "sha256sum",
    "--",
    "/usr/local/bin/agentdesk-engine"
)
if ($hashResult.ExitCode -ne 0) {
    throw "The installed WSL sidecar SHA-256 could not be calculated."
}
$hashOutput = @(Get-NormalizedWslOutput -Output @($hashResult.Output))
$installedHash = if ($hashOutput.Count -gt 0) {
    ($hashOutput[0] -split "\s+", 2)[0].ToLowerInvariant()
}
else {
    ""
}
if ($installedHash -notmatch "^[0-9a-f]{64}$" -or
    $installedHash -cne $sourceHash) {
    throw "The installed WSL sidecar SHA-256 does not match the bundled payload."
}

Write-Host "AgentDesk WSL engine installed."
Write-Host "Distribution: $selectedDistribution"
Write-Host "Path: /usr/local/bin/agentdesk-engine"
Write-Host "SHA-256: $installedHash"
