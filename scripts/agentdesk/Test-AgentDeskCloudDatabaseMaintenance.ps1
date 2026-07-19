<#
.SYNOPSIS
Runs end-to-end regression tests for offline AgentDesk Cloud SQLite backup and restore.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$backupScript = Join-Path $PSScriptRoot "Backup-AgentDeskCloudDatabase.ps1"
$restoreScript = Join-Path $PSScriptRoot "Restore-AgentDeskCloudDatabase.ps1"
$maintenanceModule = Join-Path $PSScriptRoot "AgentDesk.CloudDatabaseMaintenance.psm1"
foreach ($requiredScript in @($backupScript, $restoreScript)) {
    if (-not (Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
        throw "Cloud database maintenance script is missing: $requiredScript"
    }
}
Import-Module $maintenanceModule -Force

function Invoke-ExpectedFailure {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$MessageFragment
    )

    try {
        & $Action
    }
    catch {
        $message = [string]$_.Exception.Message
        if ($message.IndexOf(
                $MessageFragment,
                [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Expected failure containing '$MessageFragment', got: $message"
        }
        return
    }

    throw "Expected the action to fail with '$MessageFragment'."
}

function Start-CloudFixture {
    param(
        [Parameter(Mandatory)][string]$ServerPath,
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][string]$LogRoot
    )

    $environment = @{
        "AgentDeskCloud__BootstrapToken" = "agentdesk-maintenance-test-token-000000000000"
        "AgentDeskCloud__DatabasePath" = $DatabasePath
        "AgentDeskCloud__RequireHttps" = "false"
        "AgentDeskCloud__AutomationPollingIntervalSeconds" = "300"
        "ASPNETCORE_ENVIRONMENT" = "Development"
        "ASPNETCORE_URLS" = "http://127.0.0.1:0"
    }
    $previous = @{}
    foreach ($entry in $environment.GetEnumerator()) {
        $previous[$entry.Key] = [Environment]::GetEnvironmentVariable(
            $entry.Key,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            [EnvironmentVariableTarget]::Process)
    }

    $stdout = Join-Path $LogRoot ("cloud-" + [guid]::NewGuid().ToString("N") + ".stdout.log")
    $stderr = Join-Path $LogRoot ("cloud-" + [guid]::NewGuid().ToString("N") + ".stderr.log")
    try {
        $process = Start-Process `
            -FilePath $ServerPath `
            -PassThru `
            -WindowStyle Hidden `
            -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr
    }
    finally {
        foreach ($entry in $previous.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable(
                $entry.Key,
                $entry.Value,
                [EnvironmentVariableTarget]::Process)
        }
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            $details = ((Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue) +
                (Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)).Trim()
            throw "Cloud fixture exited before initialization: $details"
        }
        $lockPath = "$DatabasePath.service.lock"
        if ((Test-Path -LiteralPath $DatabasePath -PathType Leaf) -and
            (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
            $leaseHeld = $false
            try {
                $probe = [System.IO.File]::Open(
                    $lockPath,
                    [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::ReadWrite,
                    [System.IO.FileShare]::None)
                $probe.Dispose()
            }
            catch [System.IO.IOException] {
                $leaseHeld = $true
            }
            if ($leaseHeld) {
                return $process
            }
        }
        Start-Sleep -Milliseconds 100
    }

    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    throw "Cloud fixture did not initialize its SQLite database before the timeout."
}

function Stop-CloudFixture {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }
    $Process.Refresh()
    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force
        Wait-Process -Id $Process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $Process.Dispose()
}

function Write-Checksum {
    param([Parameter(Mandatory)][string]$Path)

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        "$Path.sha256",
        "$hash  $([System.IO.Path]::GetFileName($Path))`n",
        [System.Text.UTF8Encoding]::new($false))
}

function Test-TransientMaintenanceLeaseRelease {
    param([Parameter(Mandatory)][string]$FixtureRoot)

    $databasePath = Join-Path $FixtureRoot "transient-release.db"
    $lockPath = "$databasePath.service.lock"
    $readyPath = Join-Path $FixtureRoot "transient-release.ready"
    $holderPath = Join-Path $FixtureRoot "transient-release-holder.ps1"
    $holderSource = @'
param(
    [Parameter(Mandatory)][string]$LockPath,
    [Parameter(Mandatory)][string]$ReadyPath
)
$share = [System.IO.FileShare](
    [int][System.IO.FileShare]::ReadWrite -bor
    [int][System.IO.FileShare]::Delete)
$deleteHandle = $null
$pendingHandle = $null
try {
    $deleteHandle = [System.IO.FileStream]::new(
        $LockPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        $share,
        4096,
        [System.IO.FileOptions]::DeleteOnClose)
    $pendingHandle = [System.IO.FileStream]::new(
        $LockPath,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::ReadWrite,
        $share,
        4096,
        [System.IO.FileOptions]::None)
    $deleteHandle.Dispose()
    $deleteHandle = $null
    [System.IO.File]::WriteAllText($ReadyPath, "ready")
    Start-Sleep -Milliseconds 350
}
finally {
    if ($null -ne $deleteHandle) {
        $deleteHandle.Dispose()
    }
    if ($null -ne $pendingHandle) {
        $pendingHandle.Dispose()
    }
}
'@
    [System.IO.File]::WriteAllText(
        $holderPath,
        $holderSource,
        [System.Text.UTF8Encoding]::new($false))
    $holder = Start-Process `
        -FilePath (Get-Command pwsh -ErrorAction Stop).Source `
        -ArgumentList @(
            "-NoLogo",
            "-NoProfile",
            "-File",
            ('"' + $holderPath + '"'),
            "-LockPath",
            ('"' + $lockPath + '"'),
            "-ReadyPath",
            ('"' + $readyPath + '"')) `
        -PassThru `
        -WindowStyle Hidden
    try {
        $readyDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        while (-not (Test-Path -LiteralPath $readyPath -PathType Leaf)) {
            $holder.Refresh()
            if ($holder.HasExited) {
                throw "The transient maintenance lease holder exited before acquiring the lock."
            }
            if ([DateTimeOffset]::UtcNow -ge $readyDeadline) {
                throw "The transient maintenance lease holder did not acquire the lock before the timeout."
            }
            Start-Sleep -Milliseconds 25
        }

        $lease = Enter-AgentDeskCloudMaintenanceLease -DatabasePath $databasePath
        $lease.Dispose()
    }
    finally {
        if (-not $holder.HasExited) {
            Wait-Process -Id $holder.Id -Timeout 10 -ErrorAction SilentlyContinue
        }
        if (-not $holder.HasExited) {
            Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
        }
        $holder.Dispose()
    }
}

$project = Join-Path $repositoryRoot "cloud/src/AgentDesk.Cloud/AgentDesk.Cloud.csproj"
& dotnet build $project --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Cloud server build failed before database maintenance tests."
}
$serverPath = Join-Path $repositoryRoot "cloud/src/AgentDesk.Cloud/bin/Release/net10.0/AgentDesk.Cloud.exe"
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) {
    throw "Cloud server executable is missing after build: $serverPath"
}

$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "agentdesk-cloud-maintenance-test-" + [guid]::NewGuid().ToString("N"))
$databasePath = Join-Path $fixtureRoot "cloud.db"
$backupPath = Join-Path $fixtureRoot "backups/cloud-backup.db"
$rollbackDirectory = Join-Path $fixtureRoot "rollback"
$cloudProcess = $null

try {
    [System.IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $backupPath)) | Out-Null
    [System.IO.Directory]::CreateDirectory($rollbackDirectory) | Out-Null

    if ($IsWindows) {
        Test-TransientMaintenanceLeaseRelease -FixtureRoot $fixtureRoot
    }

    $cloudProcess = Start-CloudFixture `
        -ServerPath $serverPath `
        -DatabasePath $databasePath `
        -LogRoot $fixtureRoot

    Invoke-ExpectedFailure `
        -MessageFragment "service is still running" `
        -Action {
            & $backupScript `
                -DatabasePath $databasePath `
                -BackupPath $backupPath `
                -CloudServerPath $serverPath
        }
    Stop-CloudFixture -Process $cloudProcess
    $cloudProcess = $null

    & $backupScript `
        -DatabasePath $databasePath `
        -BackupPath $backupPath `
        -CloudServerPath $serverPath | Out-Null
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath "$backupPath.sha256" -PathType Leaf)) {
        throw "Offline backup did not create both the database and checksum files."
    }
    $backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumText = [System.IO.File]::ReadAllText("$backupPath.sha256").Trim()
    if ($checksumText -cne "$backupHash  $([System.IO.Path]::GetFileName($backupPath))") {
        throw "Offline backup checksum does not describe the immutable backup bytes."
    }

    Invoke-ExpectedFailure `
        -MessageFragment "fully qualified" `
        -Action {
            & $backupScript `
                -DatabasePath $databasePath `
                -BackupPath "relative-backup.db" `
                -CloudServerPath $serverPath
        }
    Invoke-ExpectedFailure `
        -MessageFragment "must differ" `
        -Action {
            & $backupScript `
                -DatabasePath $databasePath `
                -BackupPath $databasePath `
                -CloudServerPath $serverPath
        }

    $invalidDatabase = Join-Path $fixtureRoot "not-sqlite.db"
    [System.IO.File]::WriteAllText($invalidDatabase, "not a sqlite database")
    Invoke-ExpectedFailure `
        -MessageFragment "SQLite header" `
        -Action {
            & $backupScript `
                -DatabasePath $invalidDatabase `
                -BackupPath (Join-Path $fixtureRoot "backups/invalid.db") `
                -CloudServerPath $serverPath
        }

    [System.IO.File]::WriteAllText($databasePath, "damaged-current-database")
    [System.IO.File]::WriteAllText("$databasePath-wal", "stale-current-wal")
    [System.IO.File]::WriteAllText("$databasePath-shm", "stale-current-shm")
    & $restoreScript `
        -DatabasePath $databasePath `
        -BackupPath $backupPath `
        -RollbackDirectory $rollbackDirectory `
        -CloudServerPath $serverPath | Out-Null
    $restoredHash = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($restoredHash -cne $backupHash) {
        throw "Restore did not atomically install the validated backup bytes."
    }
    $rollbackFiles = @(Get-ChildItem -LiteralPath $rollbackDirectory -File -Filter "*.db")
    if ($rollbackFiles.Count -ne 1 -or
        -not (Test-Path -LiteralPath "$($rollbackFiles[0].FullName).sha256" -PathType Leaf) -or
        [System.IO.File]::ReadAllText($rollbackFiles[0].FullName) -cne "damaged-current-database") {
        throw "Restore did not preserve the previous database and checksum as a rollback copy."
    }
    foreach ($sidecar in @("wal", "shm")) {
        if (Test-Path -LiteralPath "$databasePath-$sidecar") {
            throw "Restore left a stale SQLite $sidecar file beside the restored database."
        }
        $rollbackSidecars = @(Get-ChildItem -LiteralPath $rollbackDirectory -File -Filter "*.db-$sidecar")
        if ($rollbackSidecars.Count -ne 1 -or
            -not (Test-Path -LiteralPath "$($rollbackSidecars[0].FullName).sha256" -PathType Leaf) -or
            [System.IO.File]::ReadAllText($rollbackSidecars[0].FullName) -cne "stale-current-$sidecar") {
            throw "Restore did not preserve the previous SQLite $sidecar and checksum in the rollback set."
        }
    }

    $failureRollbackDirectory = Join-Path $fixtureRoot "failure-rollback"
    [System.IO.Directory]::CreateDirectory($failureRollbackDirectory) | Out-Null
    $verifierCountPath = Join-Path $fixtureRoot "verifier-count.txt"
    $failingVerifier = Join-Path $fixtureRoot "failing-verifier.ps1"
    $escapedServerPath = $serverPath.Replace("'", "''")
    $escapedCountPath = $verifierCountPath.Replace("'", "''")
    $failingVerifierSource = @"
param([string]`$Command, [string]`$Mode, [string]`$Database)
`$count = if (Test-Path -LiteralPath '$escapedCountPath') {
    [int][System.IO.File]::ReadAllText('$escapedCountPath')
}
else { 0 }
`$count++
[System.IO.File]::WriteAllText('$escapedCountPath', [string]`$count)
if (`$count -eq 2) {
    exit 97
}
& '$escapedServerPath' `$Command `$Mode `$Database
exit `$LASTEXITCODE
"@
    [System.IO.File]::WriteAllText(
        $failingVerifier,
        $failingVerifierSource,
        [System.Text.UTF8Encoding]::new($false))
    $beforeFailedRestoreHash = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText("$databasePath-wal", "failure-original-wal")
    [System.IO.File]::WriteAllText("$databasePath-shm", "failure-original-shm")
    Invoke-ExpectedFailure `
        -MessageFragment "integrity" `
        -Action {
            & $restoreScript `
                -DatabasePath $databasePath `
                -BackupPath $backupPath `
                -RollbackDirectory $failureRollbackDirectory `
                -CloudServerPath $failingVerifier
        }
    if ((Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash -cne
        $beforeFailedRestoreHash) {
        throw "A failed post-replacement validation did not restore the original database."
    }
    foreach ($sidecar in @("wal", "shm")) {
        if ([System.IO.File]::ReadAllText("$databasePath-$sidecar") -cne
            "failure-original-$sidecar") {
            throw "A failed post-replacement validation did not restore the original SQLite $sidecar."
        }
        Remove-Item -LiteralPath "$databasePath-$sidecar" -Force
    }
    if ([System.IO.File]::ReadAllText($verifierCountPath) -cne "2") {
        throw "The restore test did not reach post-replacement integrity validation."
    }

    $tamperedBackup = Join-Path $fixtureRoot "backups/tampered.db"
    [System.IO.File]::Copy($backupPath, $tamperedBackup)
    $tamperedBytes = [System.IO.File]::ReadAllBytes($tamperedBackup)
    $tamperedBytes[$tamperedBytes.Length - 1] = $tamperedBytes[$tamperedBytes.Length - 1] -bxor 0xff
    [System.IO.File]::WriteAllBytes($tamperedBackup, $tamperedBytes)
    [System.IO.File]::WriteAllText(
        "$tamperedBackup.sha256",
        "$backupHash  $([System.IO.Path]::GetFileName($tamperedBackup))`n",
        [System.Text.UTF8Encoding]::new($false))
    Invoke-ExpectedFailure `
        -MessageFragment "SHA-256" `
        -Action {
            & $restoreScript `
                -DatabasePath $databasePath `
                -BackupPath $tamperedBackup `
                -RollbackDirectory $rollbackDirectory `
                -CloudServerPath $serverPath
        }

    $corruptBackup = Join-Path $fixtureRoot "backups/corrupt.db"
    $backupBytes = [System.IO.File]::ReadAllBytes($backupPath)
    [System.IO.File]::WriteAllBytes($corruptBackup, $backupBytes[0..255])
    Write-Checksum -Path $corruptBackup
    Invoke-ExpectedFailure `
        -MessageFragment "integrity" `
        -Action {
            & $restoreScript `
                -DatabasePath $databasePath `
                -BackupPath $corruptBackup `
                -RollbackDirectory $rollbackDirectory `
                -CloudServerPath $serverPath
        }

    $cloudProcess = Start-CloudFixture `
        -ServerPath $serverPath `
        -DatabasePath $databasePath `
        -LogRoot $fixtureRoot
    Invoke-ExpectedFailure `
        -MessageFragment "service is still running" `
        -Action {
            & $restoreScript `
                -DatabasePath $databasePath `
                -BackupPath $backupPath `
                -RollbackDirectory $rollbackDirectory `
                -CloudServerPath $serverPath
        }
}
finally {
    Stop-CloudFixture -Process $cloudProcess
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "AgentDesk Cloud database maintenance tests passed."
# GitHub Actions dot-sources pwsh steps, so expected native failures must not leak out.
$global:LASTEXITCODE = 0
