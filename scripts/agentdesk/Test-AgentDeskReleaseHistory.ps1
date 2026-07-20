<#
.SYNOPSIS
Validates AgentDesk release-history classification used by tag publication.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$modulePath = Join-Path $PSScriptRoot "AgentDesk.ReleaseHistory.psm1"
Import-Module -Name $modulePath -Force

function Assert-Equal {
    param(
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)]$Expected,
        [Parameter(Mandatory)][string]$Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message (expected '$Expected', got '$Actual')."
    }
}

function Assert-Null {
    param(
        $Value,
        [Parameter(Mandatory)][string]$Message
    )

    if ($null -ne $Value) {
        throw "$Message (got '$Value')."
    }
}

$fresh = Get-AgentDeskReleaseHistory -Releases @() -CurrentTag "v0.1.0-alpha.1"
Assert-Equal -Actual $fresh.HasPublishedHistory -Expected $false `
    -Message "An empty release list must be recognized as a first publication"
Assert-Equal -Actual $fresh.ShouldEmitFirstReleaseMarker -Expected $true `
    -Message "Only a genuinely empty history may emit a first-release marker"

$ignoredEntries = Get-AgentDeskReleaseHistory -Releases @(
    [pscustomobject]@{ tagName = "not-a-version"; isDraft = $false },
    [pscustomobject]@{ tagName = "v0.1.0-alpha.5"; isDraft = $true },
    [pscustomobject]@{ tagName = "v0.1.0-alpha.6"; isDraft = $false }
) -CurrentTag "v0.1.0-alpha.6"
Assert-Equal -Actual $ignoredEntries.HasPublishedHistory -Expected $false `
    -Message "Invalid, draft, and current tags must not become rollback history"

$legacyOnly = Get-AgentDeskReleaseHistory `
    -Releases @([pscustomobject]@{ tagName = "0.1.0-alpha.5" }) `
    -CurrentTag "v0.1.0-alpha.6"
Assert-Equal -Actual $legacyOnly.HasPublishedHistory -Expected $true `
    -Message "A supported legacy release must count as published history"
Assert-Equal -Actual $legacyOnly.HasLegacyHistory -Expected $true `
    -Message "A no-v release must be classified as legacy history"
Assert-Equal -Actual $legacyOnly.RequiresMigration -Expected $true `
    -Message "Legacy-only history must require release migration"
Assert-Null -Value $legacyOnly.PreviousRelease `
    -Message "A legacy release must never be selected as a rollback target"
Assert-Equal -Actual $legacyOnly.ShouldEmitFirstReleaseMarker -Expected $false `
    -Message "Legacy history must not produce a first-release marker"

$mixedHistory = Get-AgentDeskReleaseHistory -Releases @(
    [pscustomobject]@{ tagName = "0.1.0-alpha.5" },
    [pscustomobject]@{ tagName = "v0.1.0-alpha.4" }
) -CurrentTag "v0.1.0-alpha.6"
Assert-Equal -Actual $mixedHistory.HasLegacyHistory -Expected $true `
    -Message "Mixed history must retain the legacy-history signal"
Assert-Equal -Actual $mixedHistory.RequiresMigration -Expected $false `
    -Message "A compatible v-prefixed release must satisfy migration"
Assert-Equal -Actual $mixedHistory.PreviousRelease.TagName -Expected "v0.1.0-alpha.4" `
    -Message "Only v-prefixed releases may be selected for rollback"

$outOfOrder = Get-AgentDeskReleaseHistory -Releases @(
    [pscustomobject]@{ tagName = "0.1.0-alpha.5" },
    [pscustomobject]@{ tagName = "v0.1.0-alpha.7" }
) -CurrentTag "v0.1.0-alpha.6"
Assert-Equal -Actual $outOfOrder.BlockingPublishedRelease.TagName -Expected "v0.1.0-alpha.7" `
    -Message "A newer version must be rejected as already published"

Write-Host "AgentDesk release-history tests passed."
