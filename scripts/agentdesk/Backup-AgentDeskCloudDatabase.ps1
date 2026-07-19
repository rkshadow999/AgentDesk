<#
.SYNOPSIS
Creates a validated offline backup of the AgentDesk Cloud SQLite database.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DatabasePath,
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$CloudServerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "AgentDesk.CloudDatabaseMaintenance.psm1") -Force

$database = Resolve-AgentDeskCloudSafePath -Path $DatabasePath -MustExist
$backup = Resolve-AgentDeskCloudSafePath -Path $BackupPath
if ([string]::Equals($database, $backup, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "DatabasePath and BackupPath must differ."
}
if (Test-Path -LiteralPath $backup) {
    throw "The backup destination already exists: $backup"
}
if (Test-Path -LiteralPath "$backup.sha256") {
    throw "The backup checksum destination already exists: $backup.sha256"
}

$lease = Enter-AgentDeskCloudMaintenanceLease -DatabasePath $database
$temporaryBackup = Join-Path (Split-Path -Parent $backup) (
    "." + [System.IO.Path]::GetFileName($backup) + ".partial-" + [guid]::NewGuid().ToString("N"))
try {
    Assert-AgentDeskCloudSqliteHeader -DatabasePath $database
    Invoke-AgentDeskCloudDatabaseVerifier `
        -CloudServerPath $CloudServerPath `
        -Mode "checkpoint-and-verify" `
        -DatabasePath $database
    Copy-AgentDeskCloudFileExclusive -Source $database -Destination $temporaryBackup
    Assert-AgentDeskCloudSqliteHeader -DatabasePath $temporaryBackup
    Invoke-AgentDeskCloudDatabaseVerifier `
        -CloudServerPath $CloudServerPath `
        -Mode "verify" `
        -DatabasePath $temporaryBackup
    [System.IO.File]::Move($temporaryBackup, $backup)
    $checksum = Write-AgentDeskCloudChecksum -Path $backup
    [pscustomobject]@{
        DatabasePath = $database
        BackupPath = $backup
        ChecksumPath = $checksum
        Sha256 = Get-AgentDeskCloudSha256 -Path $backup
    }
}
catch {
    if (Test-Path -LiteralPath $temporaryBackup) {
        Remove-Item -LiteralPath $temporaryBackup -Force -ErrorAction SilentlyContinue
    }
    if ((Test-Path -LiteralPath $backup) -and
        -not (Test-Path -LiteralPath "$backup.sha256")) {
        Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    $lease.Dispose()
    if (-not $IsWindows -and (Test-Path -LiteralPath "$database.service.lock")) {
        Remove-Item -LiteralPath "$database.service.lock" -Force
    }
}
