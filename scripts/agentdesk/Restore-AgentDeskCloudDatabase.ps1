<#
.SYNOPSIS
Restores a validated AgentDesk Cloud SQLite backup while retaining a rollback copy.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DatabasePath,
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$RollbackDirectory,
    [Parameter(Mandatory)][string]$CloudServerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AgentDesk.CloudDatabaseMaintenance.psm1") -Force

$database = Resolve-AgentDeskCloudSafePath -Path $DatabasePath -MustExist
$backup = Resolve-AgentDeskCloudSafePath -Path $BackupPath -MustExist
$rollbackRoot = Resolve-AgentDeskCloudSafePath -Path $RollbackDirectory -MustExist -Directory
if ([string]::Equals($database, $backup, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "DatabasePath and BackupPath must differ."
}
if ($IsWindows -and
    [System.IO.Path]::GetPathRoot($database) -cne [System.IO.Path]::GetPathRoot($rollbackRoot)) {
    throw "RollbackDirectory must be on the same local volume as DatabasePath."
}

$lease = Enter-AgentDeskCloudMaintenanceLease -DatabasePath $database
$replacement = Join-Path (Split-Path -Parent $database) (
    "." + [System.IO.Path]::GetFileName($database) + ".restore-" + [guid]::NewGuid().ToString("N"))
$rollbackPath = Join-Path $rollbackRoot (
    "AgentDeskCloud-before-restore-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") +
    "-" + [guid]::NewGuid().ToString("N") + ".db")
$failedReplacementPath = "$rollbackPath.failed-restored.db"
$nativeReplaceBackup = "$rollbackPath.native-replace-backup"
$rollbackRestoreTemporary = Join-Path (Split-Path -Parent $database) (
    "." + [System.IO.Path]::GetFileName($database) + ".rollback-" +
    [guid]::NewGuid().ToString("N"))
$movedSidecars = [System.Collections.Generic.List[object]]::new()
$replacementAttempted = $false
$originalDatabaseHash = $null
$rollbackChecksum = $null
try {
    $backupHash = Assert-AgentDeskCloudChecksum -Path $backup
    Assert-AgentDeskCloudSqliteHeader -DatabasePath $backup
    Invoke-AgentDeskCloudDatabaseVerifier `
        -CloudServerPath $CloudServerPath `
        -Mode "verify" `
        -DatabasePath $backup
    Copy-AgentDeskCloudFileExclusive -Source $backup -Destination $replacement
    if ((Get-AgentDeskCloudSha256 -Path $replacement) -cne $backupHash) {
        throw "The staged restore copy does not match the validated backup SHA-256."
    }

    $originalDatabaseHash = Get-AgentDeskCloudSha256 -Path $database
    Copy-AgentDeskCloudFileExclusive -Source $database -Destination $rollbackPath
    if ((Get-AgentDeskCloudSha256 -Path $rollbackPath) -cne $originalDatabaseHash) {
        throw "The rollback copy does not match the active database SHA-256."
    }
    $rollbackChecksum = Write-AgentDeskCloudChecksum -Path $rollbackPath

    foreach ($suffix in @("wal", "shm")) {
        $activeSidecar = "$database-$suffix"
        if (-not (Test-Path -LiteralPath $activeSidecar -PathType Leaf)) {
            continue
        }
        $rollbackSidecar = "$rollbackPath-$suffix"
        [System.IO.File]::Move($activeSidecar, $rollbackSidecar)
        $entry = [pscustomobject]@{
            ActivePath = $activeSidecar
            RollbackPath = $rollbackSidecar
            ChecksumPath = $null
        }
        $movedSidecars.Add($entry)
        $entry.ChecksumPath = Write-AgentDeskCloudChecksum -Path $rollbackSidecar
    }

    $replacementAttempted = $true
    [System.IO.File]::Replace($replacement, $database, $nativeReplaceBackup, $true)
    $restoredHash = Get-AgentDeskCloudSha256 -Path $database
    if ($restoredHash -cne $backupHash) {
        throw "The restored database does not match the validated backup SHA-256."
    }
    Assert-AgentDeskCloudSqliteHeader -DatabasePath $database
    Invoke-AgentDeskCloudDatabaseVerifier `
        -CloudServerPath $CloudServerPath `
        -Mode "verify" `
        -DatabasePath $database
    foreach ($suffix in @("wal", "shm")) {
        $generatedSidecar = "$database-$suffix"
        if (Test-Path -LiteralPath $generatedSidecar -PathType Leaf) {
            Remove-Item -LiteralPath $generatedSidecar -Force
        }
    }
    Remove-Item -LiteralPath $nativeReplaceBackup -Force
    [pscustomobject]@{
        DatabasePath = $database
        BackupPath = $backup
        RestoredSha256 = $restoredHash
        RollbackPath = $rollbackPath
        RollbackChecksumPath = $rollbackChecksum
        RollbackSidecars = @($movedSidecars | ForEach-Object { $_.RollbackPath })
    }
}
catch {
    $operationError = $_
    if ($replacementAttempted) {
        try {
            Copy-AgentDeskCloudFileExclusive `
                -Source $rollbackPath `
                -Destination $rollbackRestoreTemporary
            if (Test-Path -LiteralPath $database -PathType Leaf) {
                [System.IO.File]::Replace(
                    $rollbackRestoreTemporary,
                    $database,
                    $failedReplacementPath,
                    $true)
                Write-AgentDeskCloudChecksum -Path $failedReplacementPath | Out-Null
            }
            else {
                [System.IO.File]::Move($rollbackRestoreTemporary, $database)
            }

            foreach ($entry in $movedSidecars) {
                if (Test-Path -LiteralPath $entry.ActivePath -PathType Leaf) {
                    [System.IO.File]::Move(
                        $entry.ActivePath,
                        "$failedReplacementPath-$([System.IO.Path]::GetExtension($entry.ActivePath).TrimStart('.'))")
                }
                Copy-AgentDeskCloudFileExclusive `
                    -Source $entry.RollbackPath `
                    -Destination $entry.ActivePath
            }
            if ((Get-AgentDeskCloudSha256 -Path $database) -cne $originalDatabaseHash) {
                throw "The automatic rollback database does not match the original SHA-256."
            }
            if (Test-Path -LiteralPath $nativeReplaceBackup) {
                Remove-Item -LiteralPath $nativeReplaceBackup -Force
            }
        }
        catch {
            throw [System.AggregateException]::new(
                "The restore failed and automatic rollback also failed. The rollback evidence remains in the rollback directory.",
                @($operationError.Exception, $_.Exception))
        }
    }
    else {
        for ($index = $movedSidecars.Count - 1; $index -ge 0; $index--) {
            $entry = $movedSidecars[$index]
            if ($entry.ChecksumPath -and (Test-Path -LiteralPath $entry.ChecksumPath)) {
                Remove-Item -LiteralPath $entry.ChecksumPath -Force -ErrorAction SilentlyContinue
            }
            if ((Test-Path -LiteralPath $entry.RollbackPath) -and
                -not (Test-Path -LiteralPath $entry.ActivePath)) {
                [System.IO.File]::Move($entry.RollbackPath, $entry.ActivePath)
            }
        }
        if ($rollbackChecksum -and (Test-Path -LiteralPath $rollbackChecksum)) {
            Remove-Item -LiteralPath $rollbackChecksum -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $rollbackPath) {
            Remove-Item -LiteralPath $rollbackPath -Force -ErrorAction SilentlyContinue
        }
    }
    throw $operationError
}
finally {
    if (Test-Path -LiteralPath $replacement) {
        Remove-Item -LiteralPath $replacement -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $rollbackRestoreTemporary) {
        Remove-Item -LiteralPath $rollbackRestoreTemporary -Force -ErrorAction SilentlyContinue
    }
    $lease.Dispose()
    if (-not $IsWindows -and (Test-Path -LiteralPath "$database.service.lock")) {
        Remove-Item -LiteralPath "$database.service.lock" -Force
    }
}
