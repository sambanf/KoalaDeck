<#
.SYNOPSIS
    Regenerates the Koala Deck artwork:
      src/KoalaDeck/Assets/KoalaDeck.ico          app icon (multi-size, PNG-compressed frames)
      installer/wizard-small-{100,150,200}.bmp   installer wizard logo (koala on the dark app tile)
.DESCRIPTION
    The koala geometry uses a 256-unit grid and matches Services/KoalaArt.cs (tray icon)
    and the KoalaLogo DrawingImage in Themes/Icons.xaml. Keep the three in sync.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
#>
param(
    [string]$IconFile = (Join-Path $PSScriptRoot '..\src\KoalaDeck\Assets\KoalaDeck.ico'),
    [string]$InstallerDir = (Join-Path $PSScriptRoot '..\installer')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Add-Ellipse([System.Drawing.Graphics]$g, [string]$hex, [float]$cx, [float]$cy, [float]$rx, [float]$ry) {
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex))
    $g.FillEllipse($brush, $cx - $rx, $cy - $ry, 2 * $rx, 2 * $ry)
    $brush.Dispose()
}

# Draws the koala into the 256 x 256 unit square of the current transform.
function Add-Koala([System.Drawing.Graphics]$g) {
    Add-Ellipse $g '#8D99AE' 56 83 48 48     # ears
    Add-Ellipse $g '#8D99AE' 200 83 48 48
    Add-Ellipse $g '#E7C6D4' 60 87 27 27     # inner ears
    Add-Ellipse $g '#E7C6D4' 196 87 27 27
    Add-Ellipse $g '#AEB8C9' 128 141 88 80   # head
    Add-Ellipse $g '#1C2029' 92 131 9 9      # eyes
    Add-Ellipse $g '#1C2029' 164 131 9 9
    Add-Ellipse $g '#2A2F3B' 128 161 25 33   # nose
    Add-Ellipse $g '#4B5466' 119 147 6 9     # nose highlight
}

function New-Graphics([System.Drawing.Bitmap]$bitmap) {
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    return $g
}

function New-KoalaPng([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = New-Graphics $bitmap
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    Add-Koala $g
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

# Koala on the rounded dark tile used in the app header, on the installer's white header background.
function Save-WizardImage([int]$size, [string]$path) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = New-Graphics $bitmap
    $g.Clear([System.Drawing.Color]::White)

    $radius = $size * 0.24
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $edge = $size - 1
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($edge - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($edge - $d, $edge - $d, $d, $d, 0, 90)
    $tile.AddArc(0, $edge - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.ColorTranslator]::FromHtml('#232A3A')), ([System.Drawing.ColorTranslator]::FromHtml('#151922'))
    $g.FillPath($fill, $tile)
    $fill.Dispose()
    $tile.Dispose()

    $padding = $size * 0.14
    $g.TranslateTransform($padding, $padding)
    $g.ScaleTransform(($size - 2 * $padding) / 256.0, ($size - 2 * $padding) / 256.0)
    Add-Koala $g
    $g.Dispose()
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $bitmap.Dispose()
    Write-Host "Wrote $path"
}

# ---- app icon -------------------------------------------------------------------------------------------------
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

$iconPath = [System.IO.Path]::GetFullPath($IconFile)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($iconPath)) | Out-Null
[System.IO.File]::WriteAllBytes($iconPath, $out.ToArray())
Write-Host "Wrote $iconPath ($($out.Length) bytes, $($sizes.Count) sizes)"

# ---- installer wizard logo (100 / 150 / 200 % DPI) ---------------------------------------------------------------
$installerPath = [System.IO.Path]::GetFullPath($InstallerDir)
[System.IO.Directory]::CreateDirectory($installerPath) | Out-Null
foreach ($scale in @(@{ Size = 55; Name = '100' }, @{ Size = 83; Name = '150' }, @{ Size = 110; Name = '200' })) {
    Save-WizardImage $scale.Size (Join-Path $installerPath "wizard-small-$($scale.Name).bmp")
}
