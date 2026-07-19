<#
.SYNOPSIS
Verifies that a native AgentDesk sidecar is a PE file for the requested architecture.

.PARAMETER Path
Path to the native Windows sidecar executable.

.PARAMETER Architecture
Expected Windows architecture: x64 or arm64.
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
    throw "Native sidecar was not found: $fullPath"
}

$stream = [System.IO.File]::Open(
    $fullPath,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::Read)
$reader = [System.IO.BinaryReader]::new($stream)
try {
    if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5a4d) {
        throw "Native sidecar is not a valid PE executable: $fullPath"
    }
    $stream.Position = 0x3c
    [int64]$peOffset = $reader.ReadInt32()
    if ($peOffset -lt 0 -or $peOffset -gt $stream.Length - 24) {
        throw "Native sidecar has an invalid PE header offset: $fullPath"
    }
    $stream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550) {
        throw "Native sidecar is missing the PE signature: $fullPath"
    }
    $actualMachine = $reader.ReadUInt16()

    $stream.Position = $peOffset + 20
    $optionalHeaderSize = $reader.ReadUInt16()
    $optionalHeaderOffset = $peOffset + 24
    $requiredOptionalHeaderBytes = 80
    if ($optionalHeaderSize -lt $requiredOptionalHeaderBytes -or
        $optionalHeaderOffset -gt $stream.Length - $requiredOptionalHeaderBytes) {
        throw "Native sidecar has an incomplete PE optional header: $fullPath"
    }

    $stream.Position = $optionalHeaderOffset
    $optionalHeaderMagic = $reader.ReadUInt16()
    if ($optionalHeaderMagic -ne 0x020b) {
        throw ("Native sidecar must use a PE32+ optional header, found 0x{0:X4}: {1}" -f
            $optionalHeaderMagic, $fullPath)
    }

    $stream.Position = $optionalHeaderOffset + 72
    $stackReserveBytes = $reader.ReadUInt64()
}
finally {
    $reader.Dispose()
    $stream.Dispose()
}

$expectedMachine = if ($Architecture -eq "arm64") { 0xaa64 } else { 0x8664 }
if ($actualMachine -ne $expectedMachine) {
    throw ("Native sidecar architecture mismatch: expected {0} (0x{1:X4}), " +
        "found PE machine 0x{2:X4}: {3}" -f
        $Architecture, $expectedMachine, $actualMachine, $fullPath)
}

$minimumStackReserveBytes = [uint64](8 * 1024 * 1024)
if ($stackReserveBytes -lt $minimumStackReserveBytes) {
    throw (("Native sidecar stack reserve is too small: require at least {0} bytes, " +
        "found {1} bytes: {2}") -f
        $minimumStackReserveBytes, $stackReserveBytes, $fullPath)
}
