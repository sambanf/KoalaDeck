<#
.SYNOPSIS
    Regenerates src/KoalaDeck/Assets/KoalaDeck.ico (multi-size, PNG-compressed frames).
.DESCRIPTION
    The koala geometry uses a 256-unit grid and matches Services/KoalaArt.cs (tray icon)
    and the KoalaLogo DrawingImage in Themes/Icons.xaml. Keep the three in sync.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\KoalaDeck\Assets\KoalaDeck.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Add-Ellipse([System.Drawing.Graphics]$g, [string]$hex, [float]$cx, [float]$cy, [float]$rx, [float]$ry) {
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex))
    $g.FillEllipse($brush, $cx - $rx, $cy - $ry, 2 * $rx, 2 * $ry)
    $brush.Dispose()
}

function New-KoalaPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    Add-Ellipse $g '#8D99AE' 56 83 48 48     # ears
    Add-Ellipse $g '#8D99AE' 200 83 48 48
    Add-Ellipse $g '#E7C6D4' 60 87 27 27     # inner ears
    Add-Ellipse $g '#E7C6D4' 196 87 27 27
    Add-Ellipse $g '#AEB8C9' 128 141 88 80   # head
    Add-Ellipse $g '#1C2029' 92 131 9 9      # eyes
    Add-Ellipse $g '#1C2029' 164 131 9 9
    Add-Ellipse $g '#2A2F3B' 128 161 25 33   # nose
    Add-Ellipse $g '#4B5466' 119 147 6 9     # nose highlight

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-KoalaPng $size) }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0)              # reserved
$writer.Write([UInt16]1)              # type: icon
$writer.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)   # width
    $writer.Write([byte]$dimension)   # height
    $writer.Write([byte]0)            # palette size
    $writer.Write([byte]0)            # reserved
    $writer.Write([UInt16]1)          # color planes
    $writer.Write([UInt16]32)         # bits per pixel
    $writer.Write([UInt32]$images[$i].Length)
    $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

$fullPath = [System.IO.Path]::GetFullPath($OutFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fullPath)) | Out-Null
[System.IO.File]::WriteAllBytes($fullPath, $out.ToArray())
Write-Host "Wrote $fullPath ($($out.Length) bytes, $($sizes.Count) sizes)"
