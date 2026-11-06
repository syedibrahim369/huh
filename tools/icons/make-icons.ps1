# Generates huh's icons (speech bubble with a "?") as multi-size .ico files with PNG entries.
#   pwsh tools/icons/make-icons.ps1
# Output: src/Huh.App/Assets/huh.ico (normal) and huh-paused.ico (grey, with a pause badge), plus a 256 px PNG for the README.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\..\src\Huh.App\Assets'))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-BubblePng([int]$size, [System.Drawing.Color]$fill, [bool]$paused) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    # Bubble: rounded square with a tail at the bottom left.
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $x = 2 * $s; $y = 2 * $s; $w = 28 * $s; $h = 22 * $s; $r = 7 * $s
    $path.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddLine($x + $w - $r, $y + $h, 13 * $s, $y + $h)
    $path.AddLine(13 * $s, $y + $h, 6 * $s, 30 * $s)
    $path.AddLine(6 * $s, 30 * $s, 8 * $s, $y + $h)
    $path.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath([System.Drawing.SolidBrush]::new($fill), $path)
    # "?" centred in the bubble body.
    $font = [System.Drawing.Font]::new('Segoe UI', [single](17 * $s), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = [System.Drawing.StringFormat]::new(); $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = [System.Drawing.RectangleF]::new($x, $y - 0.5 * $s, $w, $h)
    $g.DrawString('?', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    if ($paused) {
        # Pause badge, bottom right.
        $bx = 18 * $s; $by = 18 * $s; $bd = 13 * $s
        $g.FillEllipse([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 40, 40, 40)), $bx, $by, $bd, $bd)
        $bw = [Math]::Max(1.5, 2.2 * $s); $bh = 6 * $s
        $g.FillRectangle([System.Drawing.Brushes]::White, $bx + $bd / 2 - $bw - 0.8 * $s, $by + ($bd - $bh) / 2, $bw, $bh)
        $g.FillRectangle([System.Drawing.Brushes]::White, $bx + $bd / 2 + 0.8 * $s, $by + ($bd - $bh) / 2, $bw, $bh)
    }
    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

function Write-Ico([string]$file, [System.Drawing.Color]$fill, [bool]$paused) {
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256
    $images = foreach ($sz in $sizes) { , (New-BubblePng $sz $fill $paused) }
    $out = [System.IO.BinaryWriter]::new([System.IO.File]::Create($file))
    $out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]; $len = $images[$i].Length
        $out.Write([byte]($sz % 256)); $out.Write([byte]($sz % 256)) # 256 is stored as 0
        $out.Write([byte]0); $out.Write([byte]0)
        $out.Write([uint16]1); $out.Write([uint16]32)
        $out.Write([uint32]$len); $out.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $out.Write($img) }
    $out.Dispose()
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$blue = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
$grey = [System.Drawing.Color]::FromArgb(255, 128, 128, 128)
Write-Ico (Join-Path $OutDir 'huh.ico') $blue $false
Write-Ico (Join-Path $OutDir 'huh-paused.ico') $grey $true
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'huh-256.png'), (New-BubblePng 256 $blue $false))
"icons written to $OutDir"
