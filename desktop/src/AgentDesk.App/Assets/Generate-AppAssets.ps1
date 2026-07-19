param(
    [string]$OutputDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectangle {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Write-AgentDeskAsset {
    param(
        [string]$FileName,
        [int]$Width,
        [int]$Height,
        [int]$IconSize
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Width,
        $Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $originX = ($Width - $IconSize) / 2.0
        $originY = ($Height - $IconSize) / 2.0
        $scale = $IconSize / 256.0
        $bounds = [System.Drawing.RectangleF]::new(
            [float]$originX,
            [float]$originY,
            [float]$IconSize,
            [float]$IconSize)
        $backgroundPath = New-RoundedRectangle $bounds ([float](48 * $scale))
        $backgroundBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#181818'))
        $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $greenBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#38A169'))

        try {
            $graphics.FillPath($backgroundBrush, $backgroundPath)

            $outerA = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($originX + (62 * $scale), $originY + (181 * $scale)),
                [System.Drawing.PointF]::new($originX + (111 * $scale), $originY + (62 * $scale)),
                [System.Drawing.PointF]::new($originX + (146 * $scale), $originY + (62 * $scale)),
                [System.Drawing.PointF]::new($originX + (195 * $scale), $originY + (181 * $scale)),
                [System.Drawing.PointF]::new($originX + (161 * $scale), $originY + (181 * $scale)),
                [System.Drawing.PointF]::new($originX + (151 * $scale), $originY + (154 * $scale)),
                [System.Drawing.PointF]::new($originX + (103 * $scale), $originY + (154 * $scale)),
                [System.Drawing.PointF]::new($originX + (93 * $scale), $originY + (181 * $scale)))
            $graphics.FillPolygon($whiteBrush, $outerA)

            $counter = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($originX + (127 * $scale), $originY + (87 * $scale)),
                [System.Drawing.PointF]::new($originX + (113 * $scale), $originY + (125 * $scale)),
                [System.Drawing.PointF]::new($originX + (141 * $scale), $originY + (125 * $scale)))
            $graphics.FillPolygon($backgroundBrush, $counter)

            $accentBounds = [System.Drawing.RectangleF]::new(
                [float]($originX + (178 * $scale)),
                [float]($originY + (62 * $scale)),
                [float](18 * $scale),
                [float](68 * $scale))
            $accentPath = New-RoundedRectangle $accentBounds ([float](9 * $scale))
            try {
                $graphics.FillPath($greenBrush, $accentPath)
            }
            finally {
                $accentPath.Dispose()
            }

            $path = Join-Path $OutputDirectory $FileName
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $backgroundPath.Dispose()
            $backgroundBrush.Dispose()
            $whiteBrush.Dispose()
            $greenBrush.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
Write-AgentDeskAsset 'StoreLogo.png' 50 50 50
Write-AgentDeskAsset 'Square44x44Logo.png' 44 44 44
Write-AgentDeskAsset 'Square150x150Logo.png' 150 150 150
Write-AgentDeskAsset 'Wide310x150Logo.png' 310 150 150
Write-AgentDeskAsset 'Square310x310Logo.png' 310 310 310
