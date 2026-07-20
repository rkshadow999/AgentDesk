Set-StrictMode -Version Latest

function ConvertTo-AgentDeskReleaseVersion {
    [OutputType([Version])]
    param(
        [Parameter(Mandatory)][string]$Version
    )

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Version,
        '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(ci|alpha|beta|preview|rc)\.(0|[1-9]\d*))?$')
    if (-not $match.Success) {
        return $null
    }

    $components = foreach ($groupIndex in 1..3) {
        [uint64]$component = 0
        if (-not [uint64]::TryParse($match.Groups[$groupIndex].Value, [ref]$component) -or
            $component -gt 65535) {
            return $null
        }
        [int]$component
    }

    $revision = 65535
    if ($match.Groups[4].Success) {
        [uint64]$sequence = 0
        if (-not [uint64]::TryParse($match.Groups[5].Value, [ref]$sequence) -or
            $sequence -gt 9999) {
            return $null
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

    return [Version]::new($components[0], $components[1], $components[2], $revision)
}

function Get-AgentDeskReleaseHistory {
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Releases,
        [Parameter(Mandatory)][string]$CurrentTag
    )

    $publishedReleases = @(
        foreach ($release in @($Releases)) {
            $candidateTag = [string]$release.tagName
            if ([string]::IsNullOrWhiteSpace($candidateTag) -or
                $candidateTag -ceq $CurrentTag) {
                continue
            }
            $isDraftProperty = $release.PSObject.Properties["isDraft"]
            if ($null -ne $isDraftProperty -and [bool]$isDraftProperty.Value) {
                continue
            }

            $isLegacy = -not $candidateTag.StartsWith(
                "v",
                [System.StringComparison]::Ordinal)
            $versionText = if ($isLegacy) {
                $candidateTag
            }
            else {
                $candidateTag.Substring(1)
            }
            $candidateVersion = ConvertTo-AgentDeskReleaseVersion -Version $versionText
            if ($null -eq $candidateVersion) {
                continue
            }

            [pscustomobject]@{
                TagName = $candidateTag
                ComparableVersion = $candidateVersion
                IsLegacy = $isLegacy
            }
        }
    )
    $legacyReleases = @($publishedReleases | Where-Object IsLegacy)
    $compatibleReleases = @($publishedReleases |
        Where-Object { -not $_.IsLegacy })
    $currentComparableVersion = ConvertTo-AgentDeskReleaseVersion `
        -Version $CurrentTag.Substring(1)
    if ($null -eq $currentComparableVersion) {
        throw "Current release tag is not a supported AgentDesk version."
    }

    $blockingPublishedRelease = @($publishedReleases |
        Where-Object { $_.ComparableVersion -ge $currentComparableVersion } |
        Sort-Object ComparableVersion |
        Select-Object -First 1)
    $previousRelease = @($compatibleReleases |
        Where-Object { $_.ComparableVersion -lt $currentComparableVersion } |
        Sort-Object ComparableVersion -Descending |
        Select-Object -First 1)

    [pscustomobject]@{
        PublishedReleases = $publishedReleases
        LegacyReleases = $legacyReleases
        CompatibleReleases = $compatibleReleases
        HasPublishedHistory = $publishedReleases.Count -gt 0
        HasLegacyHistory = $legacyReleases.Count -gt 0
        BlockingPublishedRelease = if ($blockingPublishedRelease.Count -gt 0) {
            $blockingPublishedRelease[0]
        }
        else {
            $null
        }
        PreviousRelease = if ($previousRelease.Count -gt 0) {
            $previousRelease[0]
        }
        else {
            $null
        }
        RequiresMigration = $legacyReleases.Count -gt 0 -and
            $previousRelease.Count -eq 0
        ShouldEmitFirstReleaseMarker = $publishedReleases.Count -eq 0
    }
}

Export-ModuleMember -Function ConvertTo-AgentDeskReleaseVersion, Get-AgentDeskReleaseHistory
