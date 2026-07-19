Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AgentDeskCloudSafePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$MustExist,
        [switch]$Directory
    )

    if (-not [System.IO.Path]::IsPathFullyQualified($Path)) {
        throw "The maintenance path must be fully qualified: $Path"
    }
    if ($IsWindows -and (
            $Path.StartsWith("\\.\", [System.StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith("\\?\GLOBALROOT", [System.StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith("\\", [System.StringComparison]::Ordinal))) {
        throw "The maintenance path must use a local filesystem path: $Path"
    }

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ($IsWindows) {
        $rootLength = [System.IO.Path]::GetPathRoot($fullPath).Length
        if ($fullPath.IndexOf(':', $rootLength) -ge 0) {
            throw "Alternate data stream paths are not allowed for Cloud database maintenance."
        }
        $leafBase = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
        if ($leafBase -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
            throw "Windows device names are not allowed for Cloud database maintenance."
        }
    }

    $probe = if (Test-Path -LiteralPath $fullPath) {
        Get-Item -LiteralPath $fullPath -Force
    }
    else {
        Get-Item -LiteralPath (Split-Path -Parent $fullPath) -Force
    }
    while ($null -ne $probe) {
        if (($probe.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse points are not allowed in Cloud database maintenance paths: $($probe.FullName)"
        }
        $probe = if ($probe -is [System.IO.DirectoryInfo]) {
            $probe.Parent
        }
        else {
            $probe.Directory
        }
    }

    if ($MustExist) {
        $expectedType = if ($Directory) { [System.IO.DirectoryInfo] } else { [System.IO.FileInfo] }
        $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
        if ($item -isnot $expectedType) {
            throw "The maintenance path has the wrong filesystem type: $fullPath"
        }
    }
    return $fullPath
}

function Enter-AgentDeskCloudMaintenanceLease {
    param([Parameter(Mandatory)][string]$DatabasePath)

    $fileOptions = [System.IO.FileOptions]::None
    if ($IsWindows) {
        $fileOptions = [System.IO.FileOptions]::DeleteOnClose
    }
    $attemptLimit = if ($IsWindows) { 20 } else { 1 }
    for ($attempt = 1; $attempt -le $attemptLimit; $attempt++) {
        try {
            return [System.IO.FileStream]::new(
                "$DatabasePath.service.lock",
                [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None,
                4096,
                $fileOptions)
        }
        catch [System.UnauthorizedAccessException] {
            if (-not $IsWindows) {
                throw
            }
            if ($attempt -lt $attemptLimit) {
                # Delete-on-close lock names can remain briefly pending after a service exits.
                Start-Sleep -Milliseconds 50
                continue
            }
            break
        }
        catch [System.IO.IOException] {
            break
        }
    }
    throw "The AgentDesk Cloud service is still running or another maintenance operation holds the database lease. Stop the service before backup or restore."
}

function Assert-AgentDeskCloudSqliteHeader {
    param([Parameter(Mandatory)][string]$DatabasePath)

    $expected = [System.Text.Encoding]::ASCII.GetBytes("SQLite format 3`0")
    $stream = [System.IO.File]::Open(
        $DatabasePath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
    try {
        $actual = [byte[]]::new($expected.Length)
        if ($stream.Read($actual, 0, $actual.Length) -ne $actual.Length -or
            [Convert]::ToHexString($actual) -cne [Convert]::ToHexString($expected)) {
            throw "The database file does not contain the SQLite header: $DatabasePath"
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-AgentDeskCloudDatabaseVerifier {
    param(
        [Parameter(Mandatory)][string]$CloudServerPath,
        [Parameter(Mandatory)][ValidateSet("checkpoint-and-verify", "verify")][string]$Mode,
        [Parameter(Mandatory)][string]$DatabasePath
    )

    $serverPath = Resolve-AgentDeskCloudSafePath -Path $CloudServerPath -MustExist
    if ([System.IO.Path]::GetExtension($serverPath) -ieq ".dll") {
        $output = @(& dotnet $serverPath "--agentdesk-cloud-database-maintenance" $Mode $DatabasePath 2>&1)
    }
    else {
        $output = @(& $serverPath "--agentdesk-cloud-database-maintenance" $Mode $DatabasePath 2>&1)
    }
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $details = ($output | ForEach-Object { [string]$_ }) -join "`n"
        throw "SQLite integrity validation failed (exit $exitCode). $details"
    }
}

function Copy-AgentDeskCloudFileExclusive {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $inputStream = [System.IO.File]::Open(
        $Source,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    try {
        $outputStream = [System.IO.File]::Open(
            $Destination,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $inputStream.CopyTo($outputStream)
            $outputStream.Flush($true)
        }
        finally {
            $outputStream.Dispose()
        }
    }
    finally {
        $inputStream.Dispose()
    }
}

function Get-AgentDeskCloudSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-AgentDeskCloudChecksum {
    param([Parameter(Mandatory)][string]$Path)

    $checksumPath = "$Path.sha256"
    if (Test-Path -LiteralPath $checksumPath) {
        throw "The checksum destination already exists: $checksumPath"
    }
    $hash = Get-AgentDeskCloudSha256 -Path $Path
    [System.IO.File]::WriteAllText(
        $checksumPath,
        "$hash  $([System.IO.Path]::GetFileName($Path))`n",
        [System.Text.UTF8Encoding]::new($false))
    return $checksumPath
}

function Assert-AgentDeskCloudChecksum {
    param([Parameter(Mandatory)][string]$Path)

    $checksumPath = Resolve-AgentDeskCloudSafePath -Path "$Path.sha256" -MustExist
    $checksumText = [System.IO.File]::ReadAllText($checksumPath).Trim()
    $expectedName = [System.IO.Path]::GetFileName($Path)
    if ($checksumText -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\r\n]+)$' -or
        $Matches.name -cne $expectedName) {
        throw "The SHA-256 sidecar is malformed or names a different backup."
    }
    $actualHash = Get-AgentDeskCloudSha256 -Path $Path
    if ($actualHash -cne $Matches.hash) {
        throw "The backup SHA-256 does not match its sidecar."
    }
    return $actualHash
}

Export-ModuleMember -Function @(
    "Resolve-AgentDeskCloudSafePath",
    "Enter-AgentDeskCloudMaintenanceLease",
    "Assert-AgentDeskCloudSqliteHeader",
    "Invoke-AgentDeskCloudDatabaseVerifier",
    "Copy-AgentDeskCloudFileExclusive",
    "Get-AgentDeskCloudSha256",
    "Write-AgentDeskCloudChecksum",
    "Assert-AgentDeskCloudChecksum"
)
