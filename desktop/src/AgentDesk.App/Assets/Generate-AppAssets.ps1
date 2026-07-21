<#
.SYNOPSIS
Generates AgentDesk PNG tile assets and a multi-size .ico from the branded
RK source image (AgentDesk-icon-source.png).
#>
param(
    [string]$OutputDirectory = $PSScriptRoot,
    [string]$SourceImagePath = (Join-Path $PSScriptRoot "AgentDesk-icon-source.png")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path -LiteralPath $SourceImagePath)) {
    throw "Source icon image not found: $SourceImagePath"
}

function New-ScaledBitmap {
    param(
        [System.Drawing.Image]$Source,
        [int]$Width,
        [int]$Height,
        [switch]$Contain
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Width,
        $Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        if ($Contain) {
            $scale = [Math]::Min($Width / [double]$Source.Width, $Height / [double]$Source.Height)
            $drawW = [int][Math]::Round($Source.Width * $scale)
            $drawH = [int][Math]::Round($Source.Height * $scale)
            $x = [int](($Width - $drawW) / 2)
            $y = [int](($Height - $drawH) / 2)
            $dest = [System.Drawing.Rectangle]::new($x, $y, $drawW, $drawH)
            $graphics.DrawImage($Source, $dest)
        }
        else {
            $dest = [System.Drawing.Rectangle]::new(0, 0, $Width, $Height)
            $graphics.DrawImage($Source, $dest)
        }
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function Write-PngAsset {
    param(
        [System.Drawing.Image]$Source,
        [string]$FileName,
        [int]$Width,
        [int]$Height,
        [switch]$Contain
    )

    $path = Join-Path $OutputDirectory $FileName
    $bitmap = New-ScaledBitmap -Source $Source -Width $Width -Height $Height -Contain:$Contain
    try {
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "Wrote $path ($Width x $Height)"
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-MultiSizeIco {
    param(
        [System.Drawing.Image]$Source,
        [string]$FileName,
        [int[]]$Sizes = @(16, 24, 32, 48, 64, 128, 256)
    )

    $path = Join-Path $OutputDirectory $FileName
    $pngChunks = New-Object System.Collections.Generic.List[byte[]]
    foreach ($size in $Sizes) {
        $bmp = New-ScaledBitmap -Source $Source -Width $size -Height $size
        try {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngChunks.Add($ms.ToArray()) | Out-Null
            $ms.Dispose()
        }
        finally {
            $bmp.Dispose()
        }
    }

    $count = $pngChunks.Count
    $headerSize = 6
    $dirEntrySize = 16
    $offset = $headerSize + ($dirEntrySize * $count)

    $fs = [System.IO.File]::Create($path)
    try {
        $bw = New-Object System.IO.BinaryWriter $fs
        # ICONDIR
        $bw.Write([uint16]0)      # reserved
        $bw.Write([uint16]1)      # type = icon
        $bw.Write([uint16]$count) # count

        $dataOffset = $offset
        for ($i = 0; $i -lt $count; $i++) {
            $size = $Sizes[$i]
            $bytes = $pngChunks[$i]
            $w = if ($size -ge 256) { 0 } else { $size }
            $h = if ($size -ge 256) { 0 } else { $size }
            $bw.Write([byte]$w)
            $bw.Write([byte]$h)
            $bw.Write([byte]0)   # color count
            $bw.Write([byte]0)   # reserved
            $bw.Write([uint16]1) # planes
            $bw.Write([uint16]32) # bit count
            $bw.Write([uint32]$bytes.Length)
            $bw.Write([uint32]$dataOffset)
            $dataOffset += $bytes.Length
        }

        foreach ($bytes in $pngChunks) {
            $bw.Write($bytes)
        }
        $bw.Flush()
    }
    finally {
        $fs.Dispose()
    }
    Write-Host "Wrote $path (sizes: $($Sizes -join ', '))"
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$source = [System.Drawing.Image]::FromFile((Resolve-Path -LiteralPath $SourceImagePath).Path)
try {
    Write-PngAsset -Source $source -FileName "StoreLogo.png" -Width 50 -Height 50
    Write-PngAsset -Source $source -FileName "Square44x44Logo.png" -Width 44 -Height 44
    Write-PngAsset -Source $source -FileName "Square150x150Logo.png" -Width 150 -Height 150
    Write-PngAsset -Source $source -FileName "Square310x310Logo.png" -Width 310 -Height 310
    # Wide tile: dark canvas with centered brand mark
    $wide = [System.Drawing.Bitmap]::new(310, 150, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($wide)
    try {
        $g.Clear([System.Drawing.ColorTranslator]::FromHtml("#0B0E14"))
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $mark = 128
        $x = [int]((310 - $mark) / 2)
        $y = [int]((150 - $mark) / 2)
        $g.DrawImage($source, [System.Drawing.Rectangle]::new($x, $y, $mark, $mark))
        $widePath = Join-Path $OutputDirectory "Wide310x150Logo.png"
        $wide.Save($widePath, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "Wrote $widePath (310 x 150)"
    }
    finally {
        $g.Dispose()
        $wide.Dispose()
    }

    Write-MultiSizeIco -Source $source -FileName "AgentDesk.ico"
    # High-res PNG for docs / installer wizard
    Write-PngAsset -Source $source -FileName "AgentDesk-256.png" -Width 256 -Height 256
}
finally {
    $source.Dispose()
}
