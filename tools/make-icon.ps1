# Generates Assets\SysPulse.ico (16-256 px) and Assets\icon-256.png.
# Each size is drawn from vectors (not downscaled) so small sizes stay crisp.
# Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile: rounded square with a dark vertical gradient and a hairline border
    $inset = [single]([Math]::Max(0.5, $s * 0.03))
    $tw = [single]($s - 2 * $inset)
    $tile = New-RoundRect $inset $inset $tw $tw ([single]($s * 0.22))
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), ([System.Drawing.Color]::FromArgb(255, 0x24, 0x2A, 0x3A)), ([System.Drawing.Color]::FromArgb(255, 0x0E, 0x10, 0x15))
    $g.FillPath($bg, $tile)
    if ($s -ge 32) {
        $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0x34, 0x3B, 0x4D)), ([single]([Math]::Max(1, $s / 128)))
        $g.DrawPath($border, $tile)
    }

    # Faint grid lines like the app's graphs (only where there's room)
    if ($s -ge 48) {
        $grid = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(22, 255, 255, 255)), ([single]([Math]::Max(1, $s / 128)))
        foreach ($f in 0.33, 0.77) { $g.DrawLine($grid, [single]($s * 0.16), [single]($s * $f), [single]($s * 0.84), [single]($s * $f)) }
    }

    # Heartbeat / pulse line
    $norm = @(@(0.14, 0.56), @(0.33, 0.56), @(0.41, 0.42), @(0.50, 0.76), @(0.60, 0.20), @(0.69, 0.64), @(0.75, 0.56), @(0.86, 0.56))
    if ($s -le 24) { $norm = @(@(0.12, 0.56), @(0.36, 0.56), @(0.50, 0.80), @(0.62, 0.18), @(0.74, 0.56), @(0.88, 0.56)) } # simpler at tiny sizes
    $pts = [System.Drawing.PointF[]]($norm | ForEach-Object { New-Object System.Drawing.PointF ([single]($_[0] * $s)), ([single]($_[1] * $s)) })

    $cyan = [System.Drawing.Color]::FromArgb(255, 0x4F, 0xC3, 0xF7)
    $green = [System.Drawing.Color]::FromArgb(255, 0x69, 0xF0, 0xAE)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ([single]($s * 0.14)), 0), (New-Object System.Drawing.PointF ([single]($s * 0.86)), 0), $cyan, $green
    $width = [single]([Math]::Max(1.6, $s * 0.075))

    if ($s -ge 48) {
        # soft glow under the line
        foreach ($k in @(@(4.0, 10), @(3.3, 14), @(2.6, 20), @(1.9, 28))) {
            $glow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($k[1], 0x4F, 0xC3, 0xF7)), ([single]($width * $k[0]))
            $glow.LineJoin = 'Round'; $glow.StartCap = 'Round'; $glow.EndCap = 'Round'
            $g.DrawLines($glow, $pts)
        }
    }
    $pen = New-Object System.Drawing.Pen $grad, $width
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLines($pen, $pts)

    # Live "dot" at the end of the line
    if ($s -ge 32) {
        $r = [single]($width * 0.95)
        $end = $pts[$pts.Length - 1]
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $green), [single]($end.X - $r), [single]($end.Y - $r), [single]($r * 2), [single]($r * 2))
    }

    $g.Dispose()
    if ($s -eq 256) { $bmp.Save((Join-Path $assets 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    return $bmp
}

# 256 px is stored as PNG (standard); smaller sizes as classic 32-bit DIBs, which every tool can read.
function Get-IconEntry([int]$s) {
    $bmp = Get-IconPng $s
    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $bw = New-Object System.IO.BinaryWriter $ms
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
        # BITMAPINFOHEADER (height is doubled: color + AND mask)
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]($s * $s * 4 + $maskStride * $s)); $bw.Write([int32]0); $bw.Write([int32]0)
        $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {          # bottom-up BGRA
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $s)))  # AND mask all zero: alpha channel decides
        $bw.Flush()
    }
    $bmp.Dispose()
    return , $ms.ToArray()
}

# Assemble the multi-size .ico
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (Get-IconEntry $s) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'SysPulse.ico'), $out.ToArray())
"Wrote $(Join-Path $assets 'SysPulse.ico') ($($sizes -join ', ') px)"
