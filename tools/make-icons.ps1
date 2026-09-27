# Regenerates the app icons in src/KeepSoundAlive/Assets. Windows PowerShell 5.1 (uses System.Drawing).
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\KeepSoundAlive\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-Icon([int]$size, [string]$top, [string]$bottom) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Background tile
    $bg = New-RoundedRect (8 * $s) (8 * $s) (240 * $s) (240 * $s) (56 * $s)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
        ([System.Drawing.ColorTranslator]::FromHtml($top)), ([System.Drawing.ColorTranslator]::FromHtml($bottom)), 90
    $g.FillPath($brush, $bg)

    # Speaker body
    $white = [System.Drawing.Brushes]::White
    $pts = @(
        (New-Object System.Drawing.PointF (48 * $s), (100 * $s)),
        (New-Object System.Drawing.PointF (84 * $s), (100 * $s)),
        (New-Object System.Drawing.PointF (128 * $s), (58 * $s)),
        (New-Object System.Drawing.PointF (128 * $s), (198 * $s)),
        (New-Object System.Drawing.PointF (84 * $s), (156 * $s)),
        (New-Object System.Drawing.PointF (48 * $s), (156 * $s))
    )
    $g.FillPolygon($white, $pts)

    # Sound waves
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 17 * $s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, (74 * $s), (84 * $s), (88 * $s), (88 * $s), -48, 96)
    $g.DrawArc($pen, (34 * $s), (44 * $s), (168 * $s), (168 * $s), -48, 96)

    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, [string]$path) {
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$activeTop = '#34D399'; $activeBottom = '#0EA5E9'
$idleTop = '#9CA3AF'; $idleBottom = '#6B7280'

Save-Png (New-Icon 256 $activeTop $activeBottom) (Join-Path $OutDir 'icon.png')
Save-Png (New-Icon 64 $activeTop $activeBottom) (Join-Path $OutDir 'tray-on.png')
Save-Png (New-Icon 64 $idleTop $idleBottom) (Join-Path $OutDir 'tray-off.png')

# Multi-resolution .ico with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) { $images.Add((Get-PngBytes (New-Icon $size $activeTop $activeBottom))) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.ico'), $out.ToArray())
Write-Host "Icons written to $OutDir"
