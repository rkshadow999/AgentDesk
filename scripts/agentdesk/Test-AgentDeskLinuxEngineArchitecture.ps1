<#
.SYNOPSIS
Verifies that an AgentDesk WSL sidecar is a 64-bit little-endian ELF for the requested architecture.

.PARAMETER Path
Path to the Linux sidecar executable.

.PARAMETER Architecture
Expected Linux architecture: x64 or arm64.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [ValidateSet("x64", "arm64")]
    [string]$Architecture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$fullPath = [System.IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
    throw "WSL sidecar was not found: $fullPath"
}

$stream = [System.IO.File]::Open(
    $fullPath,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::Read)
$reader = [System.IO.BinaryReader]::new($stream)
try {
    if ($stream.Length -lt 20) {
        throw "WSL sidecar is not a complete ELF executable: $fullPath"
    }

    $identity = $reader.ReadBytes(7)
    if ($identity[0] -ne 0x7f -or
        $identity[1] -ne 0x45 -or
        $identity[2] -ne 0x4c -or
        $identity[3] -ne 0x46 -or
        $identity[4] -ne 2 -or
        $identity[5] -ne 1 -or
        $identity[6] -ne 1) {
        throw "WSL sidecar must be a 64-bit little-endian ELF executable: $fullPath"
    }

    $stream.Position = 16
    $elfType = $reader.ReadUInt16()
    $actualMachine = $reader.ReadUInt16()
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}

if ($elfType -notin @(2, 3)) {
    throw "WSL sidecar must be an executable or position-independent ELF file: $fullPath"
}

$expectedMachine = if ($Architecture -eq "arm64") { 0x00b7 } else { 0x003e }
if ($actualMachine -ne $expectedMachine) {
    throw ("WSL sidecar architecture mismatch: expected {0} (0x{1:X4}), " +
        "found ELF machine 0x{2:X4}: {3}" -f
        $Architecture, $expectedMachine, $actualMachine, $fullPath)
}
