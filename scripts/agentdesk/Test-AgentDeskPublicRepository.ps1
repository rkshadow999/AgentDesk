[CmdletBinding()]
param(
    [string]$BaseRevision = "c68e39f"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$failures = [System.Collections.Generic.List[string]]::new()

function Add-ValidationFailure {
    param([Parameter(Mandatory)][string]$Message)

    $failures.Add($Message)
}

function Get-RepositoryPath {
    param([Parameter(Mandatory)][string]$RelativePath)

    return Join-Path $repositoryRoot $RelativePath
}

function Read-RepositoryText {
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = Get-RepositoryPath $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }

    return [System.IO.File]::ReadAllText($path)
}

$requiredPairs = @(
    [pscustomobject]@{ English = "README.md"; Chinese = "README.zh-CN.md" }
    [pscustomobject]@{ English = "CONTRIBUTING.md"; Chinese = "CONTRIBUTING.zh-CN.md" }
    [pscustomobject]@{ English = "SECURITY.md"; Chinese = "SECURITY.zh-CN.md" }
    [pscustomobject]@{ English = "CODE_OF_CONDUCT.md"; Chinese = "CODE_OF_CONDUCT.zh-CN.md" }
    [pscustomobject]@{ English = "desktop/README.md"; Chinese = "desktop/README.zh-CN.md" }
    [pscustomobject]@{ English = "cloud/README.md"; Chinese = "cloud/README.zh-CN.md" }
    [pscustomobject]@{ English = "desktop/THIRD-PARTY-SOURCE-NOTICE.md"; Chinese = "desktop/THIRD-PARTY-SOURCE-NOTICE.zh-CN.md" }
    [pscustomobject]@{ English = "docs/INSTALLATION.md"; Chinese = "docs/INSTALLATION.zh-CN.md" }
    [pscustomobject]@{ English = "docs/ARCHITECTURE.md"; Chinese = "docs/ARCHITECTURE.zh-CN.md" }
    [pscustomobject]@{ English = "docs/AGENTDESK-THREAT-MODEL.md"; Chinese = "docs/AGENTDESK-THREAT-MODEL.zh-CN.md" }
    [pscustomobject]@{ English = "docs/BUILD-AND-TEST.md"; Chinese = "docs/BUILD-AND-TEST.zh-CN.md" }
    [pscustomobject]@{ English = "docs/RELEASING.md"; Chinese = "docs/RELEASING.zh-CN.md" }
    [pscustomobject]@{ English = "docs/ROADMAP.md"; Chinese = "docs/ROADMAP.zh-CN.md" }
    [pscustomobject]@{ English = "docs/UPSTREAM.md"; Chinese = "docs/UPSTREAM.zh-CN.md" }
)

foreach ($pair in $requiredPairs) {
    foreach ($path in @($pair.English, $pair.Chinese)) {
        if (-not (Test-Path -LiteralPath (Get-RepositoryPath $path) -PathType Leaf)) {
            Add-ValidationFailure "Required public document is missing: $path"
        }
    }

    $englishText = Read-RepositoryText $pair.English
    $chineseText = Read-RepositoryText $pair.Chinese
    if ($null -ne $englishText -and -not $englishText.Contains((Split-Path $pair.Chinese -Leaf))) {
        Add-ValidationFailure "$($pair.English) does not link to $($pair.Chinese)."
    }
    if ($null -ne $chineseText -and -not $chineseText.Contains((Split-Path $pair.English -Leaf))) {
        Add-ValidationFailure "$($pair.Chinese) does not link to $($pair.English)."
    }
}

$publicMarkdownPaths = @($requiredPairs | ForEach-Object { $_.English; $_.Chinese }) |
    Sort-Object -Unique
foreach ($relativeDocumentPath in $publicMarkdownPaths) {
    $documentText = Read-RepositoryText $relativeDocumentPath
    if ($null -eq $documentText) {
        continue
    }
    if ($documentText.Contains("rkshadow/AgentDesk")) {
        Add-ValidationFailure "$relativeDocumentPath contains the obsolete GitHub owner rkshadow/AgentDesk."
    }

    $documentDirectory = Split-Path (Get-RepositoryPath $relativeDocumentPath) -Parent
    $markdownLinks = [System.Text.RegularExpressions.Regex]::Matches(
        $documentText,
        '!?' + '\[[^\]]*\]\((?<target>[^)\s]+)(?:\s+"[^"]*")?\)')
    foreach ($markdownLink in $markdownLinks) {
        $target = $markdownLink.Groups["target"].Value.Trim('<', '>')
        if ([string]::IsNullOrWhiteSpace($target) -or
            $target.StartsWith("#", [System.StringComparison]::Ordinal) -or
            $target -match '^[A-Za-z][A-Za-z0-9+.-]*:') {
            continue
        }
        $localTarget = [Uri]::UnescapeDataString(($target -split '#', 2)[0])
        if ([string]::IsNullOrWhiteSpace($localTarget)) {
            continue
        }
        try {
            $resolvedTarget = [System.IO.Path]::GetFullPath((Join-Path $documentDirectory $localTarget))
        }
        catch {
            Add-ValidationFailure "$relativeDocumentPath contains an invalid local link: $target"
            continue
        }
        if (-not (Test-Path -LiteralPath $resolvedTarget)) {
            Add-ValidationFailure "$relativeDocumentPath contains a missing local link: $target"
        }
    }
}

$rootReadme = Read-RepositoryText "README.md"
if ($null -ne $rootReadme) {
    if ($rootReadme -notmatch '(?m)^# AgentDesk\s*$') {
        Add-ValidationFailure "README.md must use AgentDesk as its top-level product identity."
    }
    if ($rootReadme -notmatch '(?is)independent.*community-maintained.*not affiliated with or endorsed by xAI,\s*SpaceXAI,\s*OpenAI,\s*or Codex') {
        Add-ValidationFailure "README.md is missing the complete AgentDesk independence statement."
    }
    if ($rootReadme -notmatch [regex]::Escape($BaseRevision)) {
        Add-ValidationFailure "README.md must identify upstream base revision $BaseRevision."
    }

    $forbiddenReadmePatterns = @(
        '(?im)^#\s+Grok(?:\s+Build)?\s*$',
        '(?i)raw\.githubusercontent\.com/xai-org/grok-build/.*/install',
        '(?i)(?:src|href)=["''][^"'']*(?:xai|grok)[^"'']*(?:logo|wordmark)'
    )
    foreach ($pattern in $forbiddenReadmePatterns) {
        if ($rootReadme -match $pattern) {
            Add-ValidationFailure "README.md retains forbidden upstream product branding or installation content."
            break
        }
    }
}

$securityReportUrl = "https://github.com/rkshadow999/AgentDesk/security/advisories/new"
foreach ($path in @("SECURITY.md", "SECURITY.zh-CN.md")) {
    $text = Read-RepositoryText $path
    if ($null -ne $text -and -not $text.Contains($securityReportUrl)) {
        Add-ValidationFailure "$path must direct private reports to $securityReportUrl."
    }
}

$contributionUrls = @(
    "https://github.com/rkshadow999/AgentDesk/issues",
    "https://github.com/rkshadow999/AgentDesk/pulls"
)
foreach ($path in @("CONTRIBUTING.md", "CONTRIBUTING.zh-CN.md")) {
    $text = Read-RepositoryText $path
    if ($null -eq $text) {
        continue
    }
    foreach ($url in $contributionUrls) {
        if (-not $text.Contains($url)) {
            Add-ValidationFailure "$path must link to $url."
        }
    }
}

$requiredCommunityFiles = @(
    ".github/ISSUE_TEMPLATE/bug_report.yml",
    ".github/ISSUE_TEMPLATE/feature_request.yml",
    ".github/ISSUE_TEMPLATE/config.yml",
    ".github/PULL_REQUEST_TEMPLATE.md",
    ".github/dependabot.yml"
)
foreach ($path in $requiredCommunityFiles) {
    if (-not (Test-Path -LiteralPath (Get-RepositoryPath $path) -PathType Leaf)) {
        Add-ValidationFailure "Required GitHub community file is missing: $path"
    }
}

$issueTemplateConfig = Read-RepositoryText ".github/ISSUE_TEMPLATE/config.yml"
if ($null -ne $issueTemplateConfig -and -not $issueTemplateConfig.Contains($securityReportUrl)) {
    Add-ValidationFailure ".github/ISSUE_TEMPLATE/config.yml must direct security reports to Private Vulnerability Reporting."
}

$pullRequestTemplate = Read-RepositoryText ".github/PULL_REQUEST_TEMPLATE.md"
if ($null -ne $pullRequestTemplate) {
    $requiredPullRequestTexts = @(
        [regex]::Unescape('Tests / \u6D4B\u8BD5')
        [regex]::Unescape('Security and privacy / \u5B89\u5168\u4E0E\u9690\u79C1')
        [regex]::Unescape('Documentation / \u6587\u6863')
    )
    foreach ($requiredPullRequestText in $requiredPullRequestTexts) {
        if (-not $pullRequestTemplate.Contains($requiredPullRequestText)) {
            Add-ValidationFailure ".github/PULL_REQUEST_TEMPLATE.md is missing '$requiredPullRequestText'."
        }
    }
}

$gitignorePath = Get-RepositoryPath ".gitignore"
$gitignoreRules = if (Test-Path -LiteralPath $gitignorePath -PathType Leaf) {
    @(Get-Content -LiteralPath $gitignorePath | ForEach-Object { $_.Trim() })
}
else {
    @()
    Add-ValidationFailure ".gitignore is missing."
}

$requiredIgnoreRules = @(
    ".env",
    ".env.*",
    "!.env.example",
    "*.pfx",
    "*.p12",
    "*.pem",
    "*.key",
    "*.snk",
    ".superpowers/",
    "cloud/**/bin/",
    "cloud/**/obj/"
)
foreach ($rule in $requiredIgnoreRules) {
    if ($gitignoreRules -notcontains $rule) {
        Add-ValidationFailure ".gitignore is missing required secret guard: $rule"
    }
}

$gitAttributes = Read-RepositoryText ".gitattributes"
if ($null -eq $gitAttributes) {
    Add-ValidationFailure ".gitattributes is missing."
}
else {
    foreach ($requiredAttribute in @(
        "*.rs text eol=lf",
        "*.md text eol=lf",
        "*.json text eol=lf",
        "*.yml text eol=lf",
        "*.ps1 text eol=lf",
        "*.sln text eol=crlf",
        "*.msix binary"
    )) {
        if (-not $gitAttributes.Contains($requiredAttribute)) {
            Add-ValidationFailure ".gitattributes is missing '$requiredAttribute'."
        }
    }
}

$previousErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = "Continue"
    $baseCommit = @(& git -C $repositoryRoot rev-parse --verify "$BaseRevision^{commit}" 2>$null)
    $baseRevisionExitCode = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousErrorActionPreference
}

if ($baseRevisionExitCode -ne 0 -or [string]::IsNullOrWhiteSpace(($baseCommit -join ""))) {
    Add-ValidationFailure "Unable to resolve upstream base revision: $BaseRevision"
}
else {
    try {
        $ErrorActionPreference = "Continue"
        $changedRustEntries = @(& git -C $repositoryRoot diff --name-status -M -C --diff-filter=ACMRT $BaseRevision -- '*.rs' 2>$null)
        $rustDiffExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($rustDiffExitCode -ne 0) {
        Add-ValidationFailure "Unable to enumerate modified Rust files from $BaseRevision."
    }
    else {
        $requiredNotice = "// Modified by the AgentDesk project for Windows desktop integration and safety support."
        foreach ($changedEntry in $changedRustEntries) {
            if ([string]::IsNullOrWhiteSpace($changedEntry)) {
                continue
            }

            $columns = @($changedEntry -split "`t")
            if ($columns.Count -lt 2) {
                Add-ValidationFailure "Unable to parse changed Rust path entry: $changedEntry"
                continue
            }
            $status = $columns[0]
            if (($status.StartsWith("R") -or $status.StartsWith("C")) -and $columns.Count -ge 3) {
                $upstreamPath = $columns[1].Trim()
                $relativePath = $columns[2].Trim()
            }
            else {
                $upstreamPath = $columns[1].Trim()
                $relativePath = $upstreamPath
            }
            try {
                $ErrorActionPreference = "Continue"
                & git -C $repositoryRoot cat-file -e "${BaseRevision}:$upstreamPath" 2>$null
                $pathExistsInUpstream = $LASTEXITCODE -eq 0
            }
            finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }
            if (-not $pathExistsInUpstream) {
                # AgentDesk-only Rust files were not modified from upstream.
                continue
            }

            $path = Get-RepositoryPath $relativePath
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Add-ValidationFailure "Modified Rust source is missing from the worktree: $relativePath"
                continue
            }

            $firstLine = [System.IO.File]::ReadLines($path) | Select-Object -First 1
            if ($null -eq $firstLine -or $firstLine.TrimStart([char]0xFEFF) -cne $requiredNotice) {
                Add-ValidationFailure "Modified upstream Rust source is missing the leading AgentDesk notice: $relativePath"
            }
        }
    }
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Host "[FAIL] $failure" -ForegroundColor Red
    }
    throw "AgentDesk public repository contract failed with $($failures.Count) violation(s)."
}

Write-Host "AgentDesk public repository contract passed." -ForegroundColor Green
