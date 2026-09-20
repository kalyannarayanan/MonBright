<#
    make-icon.ps1 - regenerates assets\icon.png (1024x1024).

    Draws a Fluent-styled app icon rather than shipping opaque binary art, so
    it can be tweaked in source control. Deliberately has no rounded-square
    container: that squircle is a macOS convention, and Windows app icons sit
    directly on the desktop background.

    Keep this file pure ASCII - PowerShell 5.1 reads a BOM-less .ps1 using the
    ANSI codepage, where a UTF-8 em dash decodes into a curly quote that the
    parser treats as a string delimiter.

    Run:  .\assets\make-icon.ps1        then  .\build.ps1   to re-embed it.
#>

[CmdletBinding()]
param([string]$Destination)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $Destination) { $Destination = Join-Path $PSScriptRoot 'icon.png' }

$N = 1024
$bmp = New-Object System.Drawing.Bitmap($N, $N)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

function C([int]$r, [int]$gr, [int]$b, [int]$a = 255) {
    return [System.Drawing.Color]::FromArgb($a, $r, $gr, $b)
}

function Get-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# --- Contact shadow under the stand -----------------------------------------
$shadow = New-Object System.Drawing.Drawing2D.GraphicsPath
$shadow.AddEllipse(250, 826, 524, 78)
$sb = New-Object System.Drawing.Drawing2D.PathGradientBrush($shadow)
$sb.CenterColor = (C 0 0 0 105)
$sb.SurroundColors = @((C 0 0 0 0))
$g.FillPath($sb, $shadow)
$sb.Dispose(); $shadow.Dispose()

# --- Stand ------------------------------------------------------------------
$neck = New-Object System.Drawing.Drawing2D.GraphicsPath
$neck.AddPolygon(@(
    (New-Object System.Drawing.PointF(468, 690)),
    (New-Object System.Drawing.PointF(556, 690)),
    (New-Object System.Drawing.PointF(574, 792)),
    (New-Object System.Drawing.PointF(450, 792))
))
$neckRect = New-Object System.Drawing.Rectangle 450, 690, 124, 102
$neckBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($neckRect, (C 71 85 105), (C 45 55 72), 90.0)
$g.FillPath($neckBrush, $neck)
$neckBrush.Dispose(); $neck.Dispose()

$baseRect = New-Object System.Drawing.Rectangle 338, 786, 348, 62
$basePath = Get-RoundedPath 338 786 348 62 31
$baseBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($baseRect, (C 82 96 118), (C 39 48 63), 90.0)
$g.FillPath($baseBrush, $basePath)
$baseBrush.Dispose(); $basePath.Dispose()

# --- Monitor body -----------------------------------------------------------
$bodyRect = New-Object System.Drawing.Rectangle 62, 142, 900, 566
$bodyPath = Get-RoundedPath 62 142 900 566 62
$bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bodyRect, (C 62 76 96), (C 26 33 45), 90.0)
$g.FillPath($bodyBrush, $bodyPath)
$bodyBrush.Dispose()

# Hairline highlight along the top edge gives the bezel some depth.
$rimPen = New-Object System.Drawing.Pen((C 255 255 255 46), 5)
$g.DrawPath($rimPen, $bodyPath)
$rimPen.Dispose(); $bodyPath.Dispose()

# --- Screen -----------------------------------------------------------------
$scrRect = New-Object System.Drawing.Rectangle 112, 192, 800, 430
$scrPath = Get-RoundedPath 112 192 800 430 34
$scrBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($scrRect, (C 10 86 200), (C 56 176 255), 55.0)
$g.FillPath($scrBrush, $scrPath)
$scrBrush.Dispose()

$g.SetClip($scrPath)

# Glow behind the sun, clipped to the screen so light stays on the panel.
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(202, 97, 620, 620)
$glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$glowBrush.CenterColor = (C 255 240 190 165)
$glowBrush.SurroundColors = @((C 255 240 190 0))
$g.FillPath($glowBrush, $glow)
$glowBrush.Dispose(); $glow.Dispose()

# --- Sun --------------------------------------------------------------------
$cx = 512.0; $cy = 407.0; $rDisc = 96.0
$rayPen = New-Object System.Drawing.Pen((C 255 214 84), 34)
$rayPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$rayPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
for ($i = 0; $i -lt 8; $i++) {
    $a = $i * [Math]::PI / 4.0
    $x1 = $cx + [Math]::Cos($a) * ($rDisc + 44)
    $y1 = $cy + [Math]::Sin($a) * ($rDisc + 44)
    $x2 = $cx + [Math]::Cos($a) * ($rDisc + 104)
    $y2 = $cy + [Math]::Sin($a) * ($rDisc + 104)
    $g.DrawLine($rayPen, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
}
$rayPen.Dispose()

$discPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$discPath.AddEllipse([single]($cx - $rDisc), [single]($cy - $rDisc), [single]($rDisc * 2), [single]($rDisc * 2))
$discBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush($discPath)
$discBrush.CenterPoint = New-Object System.Drawing.PointF([single]($cx - 30), [single]($cy - 34))
$discBrush.CenterColor = (C 255 248 208)
$discBrush.SurroundColors = @((C 255 176 24))
$g.FillPath($discBrush, $discPath)
$discBrush.Dispose(); $discPath.Dispose()

$g.ResetClip()
$scrPath.Dispose()

$g.Dispose()
$bmp.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "Wrote $Destination" -ForegroundColor Green
