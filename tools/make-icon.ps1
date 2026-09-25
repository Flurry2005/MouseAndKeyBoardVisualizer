# Generates src/MouseSwipeVisualizer/Assets/AppIcon.ico: the swipe-arrow icon (white arrow on a dark
# circle) at 16-256 px, PNG-compressed entries. Run from anywhere: powershell -File tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'src\MouseSwipeVisualizer\Assets\AppIcon.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

function New-IconPng([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $s = $size / 32.0
        $g.ScaleTransform([single]$s, [single]$s)

        $background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 28, 30, 38))
        $g.FillEllipse($background, 1, 1, 30, 30)
        if ($size -ge 32) {
            # Thin light ring so the icon stays visible on dark taskbars / Start menus.
            $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 78, 84, 102)), ([single](0.9))
            $g.DrawEllipse($ring, 1.45, 1.45, 29.1, 29.1)
            $ring.Dispose()
        }

        $width = if ($size -le 20) { 3.6 } else { 3.2 }
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]$width)
        $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $cap = New-Object System.Drawing.Drawing2D.AdjustableArrowCap 2.6, 2.6
        $pen.CustomEndCap = $cap
        $g.DrawBezier($pen, 7, 23, 12, 22, 16, 12, 24, 9)
        $cap.Dispose(); $pen.Dispose(); $background.Dispose()
    }
    finally { $g.Dispose() }

    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return ,$stream.ToArray()
}

$images = foreach ($size in $sizes) { ,(New-IconPng $size) }
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$images[$i].Length); $writer.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($png in $images) { $writer.Write([byte[]]$png) }
$writer.Flush()
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
Write-Host "Wrote $target ($($out.Length) bytes, sizes $($sizes -join ', '))"
