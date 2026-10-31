<#
.SYNOPSIS
  Renders the AppDock icon at every size Windows uses and writes assets/AppDock.ico (PNG-compressed frames).
  Design: violet-blue rounded tile, a white screen outline with a solid panel docked on its right edge.
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\assets\AppDock.ico'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [Math]::Max(0.01, $r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$s) {
    $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile
    $inset = [Math]::Max(0.5, $s * 0.03)
    $tile = New-RoundedPath $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.22)
    $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($s, $s),
        [System.Drawing.Color]::FromArgb(255, 72, 118, 255), [System.Drawing.Color]::FromArgb(255, 132, 84, 240))
    $g.FillPath($grad, $tile)

    # Screen outline
    $m = $s * 0.2
    $sw = $s - 2 * $m
    $sh = $sw * 0.78
    $sy = ($s - $sh) / 2
    $stroke = [Math]::Max(1.0, $s * 0.06)
    $white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $pen = [System.Drawing.Pen]::new($white, $stroke)
    $pen.LineJoin = 'Round'
    $screen = New-RoundedPath $m $sy $sw $sh ($s * 0.06)
    $g.DrawPath($pen, $screen)

    # Docked panel on the right
    $panelW = $sw * 0.36
    $panel = New-RoundedPath ($m + $sw - $panelW) $sy $panelW $sh ($s * 0.05)
    $g.FillPath([System.Drawing.SolidBrush]::new($white), $panel)

    # Hint of content lines in the main area (only where there is room)
    if ($s -ge 32) {
        $soft = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(150, 255, 255, 255))
        $lx = $m + $stroke * 1.6
        $lw = $sw - $panelW - $stroke * 3.2
        foreach ($i in 0..2) {
            $ly = $sy + $stroke * 1.8 + $i * ($sh * 0.2)
            $g.FillRectangle($soft, $lx, $ly, $lw * (1 - 0.25 * $i), [Math]::Max(1, $s * 0.035))
        }
    }

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = $sizes | ForEach-Object { , (Render $_) }

$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | Join-Path -ChildPath (Split-Path $Out -Leaf)))
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $frames[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Dispose()

# Also keep a PNG for docs and the About box.
[System.IO.File]::WriteAllBytes((Join-Path (Split-Path $Out) 'AppDock.png'), $frames[-1])
Write-Host "Wrote $Out"
