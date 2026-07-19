<#
.SYNOPSIS
Runs focused regression tests for the AgentDesk WSL sidecar installer.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$installer = Join-Path $PSScriptRoot "Install-AgentDeskWslEngine.ps1"
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-wsl-installer-test-" + [guid]::NewGuid().ToString("N"))

function New-WslRunner {
    param(
        [Parameter(Mandatory)][string[]]$InstalledDistributions,
        [Parameter(Mandatory)][string]$UserId,
        [Parameter(Mandatory)][string]$InstalledHash,
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Calls
    )

    $installed = [string[]]$InstalledDistributions.Clone()
    $warning = [System.Management.Automation.ErrorRecord]::new(
        [System.Exception]::new("simulated WSL stderr warning"),
        "AgentDeskWslWarning",
        [System.Management.Automation.ErrorCategory]::NotSpecified,
        $null)
    return {
        param([string[]]$Arguments)

        $Calls.Add([string[]]$Arguments.Clone())
        $command = $Arguments -join " "
        if ($command -eq "--list --quiet") {
            return [pscustomobject]@{
                ExitCode = 0
                Output = @($warning) + $installed
            }
        }
        if ($command -match "--exec wslpath") {
            return [pscustomobject]@{
                ExitCode = 0
                Output = @($warning, "/mnt/c/agentdesk-engine")
            }
        }
        if ($command -match "--exec id -u") {
            return [pscustomobject]@{
                ExitCode = 0
                Output = @($warning, $UserId)
            }
        }
        if ($command -match "--exec sha256sum") {
            return [pscustomobject]@{
                ExitCode = 0
                Output = @($warning, "$InstalledHash  /usr/local/bin/agentdesk-engine")
            }
        }
        return [pscustomobject]@{
            ExitCode = 0
            Output = @()
        }
    }.GetNewClosure()
}

function Assert-DistributionCalls {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Calls,
        [Parameter(Mandatory)][string]$ExpectedDistribution
    )

    $distributionCalls = @($Calls | Where-Object {
        $_.Count -gt 0 -and $_[0] -ne "--list"
    })
    if ($distributionCalls.Count -eq 0) {
        throw "The WSL installer did not invoke the selected distribution."
    }
    foreach ($call in $distributionCalls) {
        if ($call.Count -lt 2 -or
            $call[0] -ne "--distribution" -or
            $call[1] -ne $ExpectedDistribution) {
            throw "The WSL installer targeted the wrong distribution: $($call -join ' ')"
        }
        if ($call.Count -lt 3 -or $call[2] -ne "--exec") {
            throw "The WSL installer did not preserve the command argument boundary: $($call -join ' ')"
        }
    }
    return $distributionCalls
}

function Assert-InstallerThrows {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessagePattern
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $MessagePattern) {
            throw
        }
        return
    }
    throw "The WSL installer was expected to fail with '$MessagePattern'."
}

try {
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $sourcePath = Join-Path $fixtureRoot "agentdesk-engine"
    [System.IO.File]::WriteAllBytes($sourcePath, [byte[]](0x7f, 0x45, 0x4c, 0x46))
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash.ToLowerInvariant()

    $calls = [System.Collections.Generic.List[object]]::new()
    $runner = New-WslRunner -InstalledDistributions @("docker-desktop", "Ubuntu") -UserId "1000" -InstalledHash $sourceHash -Calls $calls
    & $installer -SourcePath $sourcePath -DistributionName "" -WslCommandRunner $runner
    $distributionCalls = Assert-DistributionCalls -Calls $calls -ExpectedDistribution "Ubuntu"
    $sudoInstallCalls = @($distributionCalls | Where-Object {
        ($_ -join " ") -match "--exec sudo install -m 0755"
    })
    if ($sudoInstallCalls.Count -ne 1) {
        throw "A non-root WSL user must install the sidecar through sudo."
    }
    $hashCalls = @($distributionCalls | Where-Object {
        ($_ -join " ") -match "--exec sha256sum -- /usr/local/bin/agentdesk-engine"
    })
    if ($hashCalls.Count -ne 1) {
        throw "The WSL installer must verify the installed payload SHA-256."
    }

    $calls = [System.Collections.Generic.List[object]]::new()
    $runner = New-WslRunner -InstalledDistributions @("Ubuntu", "Debian") -UserId "0" -InstalledHash $sourceHash -Calls $calls
    & $installer -SourcePath $sourcePath -DistributionName "Debian" -WslCommandRunner $runner
    $distributionCalls = Assert-DistributionCalls -Calls $calls -ExpectedDistribution "Debian"
    $rootInstallCalls = @($distributionCalls | Where-Object {
        ($_ -join " ") -match "--exec install -m 0755" -and
        ($_ -join " ") -notmatch "--exec sudo install"
    })
    if ($rootInstallCalls.Count -ne 1) {
        throw "A root WSL user must install without requiring sudo."
    }

    $calls = [System.Collections.Generic.List[object]]::new()
    $runner = New-WslRunner -InstalledDistributions @("docker-desktop") -UserId "0" -InstalledHash $sourceHash -Calls $calls
    Assert-InstallerThrows -MessagePattern "exactly one installed non-Docker" -Action {
        & $installer -SourcePath $sourcePath -DistributionName "" -WslCommandRunner $runner
    }

    $calls = [System.Collections.Generic.List[object]]::new()
    $runner = New-WslRunner -InstalledDistributions @("Ubuntu", "Debian") -UserId "0" -InstalledHash $sourceHash -Calls $calls
    Assert-InstallerThrows -MessagePattern "exactly one installed non-Docker" -Action {
        & $installer -SourcePath $sourcePath -DistributionName "" -WslCommandRunner $runner
    }

    $calls = [System.Collections.Generic.List[object]]::new()
    $runner = New-WslRunner -InstalledDistributions @("Ubuntu") -UserId "0" -InstalledHash ("0" * 64) -Calls $calls
    Assert-InstallerThrows -MessagePattern "SHA-256 does not match" -Action {
        & $installer -SourcePath $sourcePath -DistributionName "" -WslCommandRunner $runner
    }
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "AgentDesk WSL installer regression tests passed."
