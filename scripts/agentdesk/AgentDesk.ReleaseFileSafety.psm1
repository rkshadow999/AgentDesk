Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-AgentDeskReleaseFullPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $Path))
}

function Get-AgentDeskStableSnapshotSha256 {
    param([Parameter(Mandatory)][object]$Snapshot)

    $stream = [System.IO.FileStream]$Snapshot.Stream
    $originalPosition = $stream.Position
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream.Position = 0
        return [Convert]::ToHexStringLower($hasher.ComputeHash($stream))
    }
    finally {
        $stream.Position = $originalPosition
        $hasher.Dispose()
    }
}

function New-AgentDeskStableFileSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][long]$MaximumBytes,
        [Parameter(Mandatory)][string]$Description
    )

    if ($MaximumBytes -le 0) {
        throw "The stable snapshot maximum size must be positive."
    }
    if ([System.IO.Path]::GetFileName($FileName) -cne $FileName -or
        $FileName -in @(".", "..") -or
        $FileName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "The stable snapshot file name is unsafe: $FileName"
    }

    $sourceFullPath = Get-AgentDeskReleaseFullPath $SourcePath
    $sourceInformation = Get-Item -LiteralPath $sourceFullPath -Force -ErrorAction SilentlyContinue
    if ($null -eq $sourceInformation -or
        $sourceInformation.PSIsContainer -or
        $sourceInformation.Length -le 0 -or
        $sourceInformation.Length -gt $MaximumBytes -or
        ($sourceInformation.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The $Description is missing, empty, oversized, or a reparse point."
    }

    $stageFullRoot = Get-AgentDeskReleaseFullPath $StageRoot
    [System.IO.Directory]::CreateDirectory($stageFullRoot) | Out-Null
    if (([System.IO.File]::GetAttributes($stageFullRoot) -band
            [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The stable release staging directory must not be a reparse point."
    }
    $stagePath = Join-Path $stageFullRoot $FileName

    $sourceStream = $null
    $stageWriteStream = $null
    $stageReadStream = $null
    try {
        $sourceStream = [System.IO.File]::Open(
            $sourceFullPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        if ($sourceStream.Length -le 0 -or $sourceStream.Length -gt $MaximumBytes) {
            throw "The $Description changed to an invalid size before staging."
        }

        $stageWriteStream = [System.IO.File]::Open(
            $stagePath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::Read)
        $sourceStream.CopyTo($stageWriteStream)
        $stageWriteStream.Flush($true)
        if ($stageWriteStream.Length -ne $sourceStream.Length) {
            throw "The $Description changed while it was copied to stable staging."
        }
        $stageWriteStream.Dispose()
        $stageWriteStream = $null

        $stageReadStream = [System.IO.File]::Open(
            $stagePath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            [System.IO.FileShare]::Read)
        $snapshot = [pscustomobject]@{
            PSTypeName = "AgentDesk.StableFileSnapshot"
            SourcePath = $sourceFullPath
            Path = $stagePath
            Stream = $stageReadStream
            Length = [long]$stageReadStream.Length
            Sha256 = $null
            Description = $Description
        }
        $snapshot.Sha256 = Get-AgentDeskStableSnapshotSha256 -Snapshot $snapshot
        $stageReadStream.Position = 0
        $stageReadStream = $null
        return $snapshot
    }
    finally {
        if ($null -ne $sourceStream) {
            $sourceStream.Dispose()
        }
        if ($null -ne $stageWriteStream) {
            $stageWriteStream.Dispose()
            Remove-Item -LiteralPath $stagePath -Force -ErrorAction SilentlyContinue
        }
        if ($null -ne $stageReadStream) {
            $stageReadStream.Dispose()
            Remove-Item -LiteralPath $stagePath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Read-AgentDeskStableSnapshotText {
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][int]$MaximumCharacters
    )

    if ($MaximumCharacters -le 0 -or $Snapshot.Length -gt $MaximumCharacters * 4L) {
        throw "The stable snapshot text is oversized."
    }
    $stream = [System.IO.FileStream]$Snapshot.Stream
    $reader = [System.IO.StreamReader]::new(
        $stream,
        [System.Text.UTF8Encoding]::new($false, $true),
        $true,
        4096,
        $true)
    try {
        $stream.Position = 0
        $text = $reader.ReadToEnd()
        if ($text.Length -gt $MaximumCharacters) {
            throw "The stable snapshot text is oversized."
        }
        return $text
    }
    finally {
        $reader.Dispose()
        $stream.Position = 0
    }
}

function Close-AgentDeskStableFileSnapshot {
    param([Parameter(Mandatory)][object]$Snapshot)

    if ($null -ne $Snapshot.Stream) {
        $Snapshot.Stream.Dispose()
        $Snapshot.Stream = $null
    }
}

Export-ModuleMember -Function @(
    "New-AgentDeskStableFileSnapshot",
    "Get-AgentDeskStableSnapshotSha256",
    "Read-AgentDeskStableSnapshotText",
    "Close-AgentDeskStableFileSnapshot"
)
