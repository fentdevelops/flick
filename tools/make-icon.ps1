# Draws Flick's app icon at every Windows size and packs it into assets\flick.ico, plus docs\icon.png.
# A grey tile with real depth: lit from above, bevelled, viewfinder corners around a raised grey button.
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\assets\flick.ico'),
    [string]$Png = (Join-Path $PSScriptRoot '..\docs\icon.png')
)
Add-Type -AssemblyName System.Drawing

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Argb($a, $r, $g, $b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function Pt([single]$x, [single]$y) { New-Object System.Drawing.PointF $x, $y }
function Vert($y0, $y1, $c0, $c1) { New-Object System.Drawing.Drawing2D.LinearGradientBrush (Pt 0 $y0), (Pt 0 $y1), $c0, $c1 }

function Corners($g, $pen, [single]$s, [single]$offset) {
    $a = [single]($s * 0.255); $b = [single]($s * 0.745); $l = [single]($s * 0.135)
    foreach ($c in @(@($a, $a, 1, 1), @($b, $a, -1, 1), @($a, $b, 1, -1), @($b, $b, -1, -1))) {
        $x = [single]$c[0]; $y = [single]($c[1] + $offset); $dx = [single]($c[2] * $l); $dy = [single]($c[3] * $l)
        $g.DrawLines($pen, [System.Drawing.PointF[]]@((Pt $x ($y + $dy)), (Pt $x $y), (Pt ($x + $dx) $y)))
    }
}

function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $detail = $s -ge 32

    $m = if ($detail) { [single]($s * 0.06) } else { [single]($s * 0.03) }
    $tw = [single]($s - 2 * $m)
    $r = [single]($tw * 0.235)

    # Soft shadow under the tile.
    if ($detail) {
        foreach ($i in 5, 3, 1) {
            $grow = [single]($s * 0.004 * $i)
            $sh = RoundRect ($m - $grow) ($m + $s * 0.018 - $grow) ($tw + 2 * $grow) ($tw + 2 * $grow) ($r + $grow)
            $g.FillPath((New-Object System.Drawing.SolidBrush (Argb 18 0 0 0)), $sh)
        }
    }

    # Tile, lit from above.
    $tile = RoundRect $m $m $tw $tw $r
    $g.FillPath((Vert $m ($m + $tw) (Argb 255 124 124 129) (Argb 255 44 44 47)), $tile)

    if ($detail) {
        # Sheen on the upper half, then a bright top bevel fading into a dark lower edge.
        $state = $g.Save()
        $g.SetClip($tile)
        $sheen = New-Object System.Drawing.Drawing2D.GraphicsPath
        $sheen.AddEllipse([single]($m - $tw * 0.25), [single]($m - $tw * 0.62), [single]($tw * 1.5), [single]($tw * 1.08))
        $g.FillPath((Vert $m ($m + $tw * 0.46) (Argb 40 255 255 255) (Argb 4 255 255 255)), $sheen)
        $g.Restore($state)

        $bw = [single][Math]::Max(1, $s / 64)
        $inset = [single]($bw / 2)
        $edge = RoundRect ($m + $inset) ($m + $inset) ($tw - $bw) ($tw - $bw) ($r - $inset)
        $bevel = New-Object System.Drawing.Pen ((Vert $m ($m + $tw) (Argb 170 255 255 255) (Argb 70 0 0 0))), $bw
        $g.DrawPath($bevel, $edge)
    }

    # Viewfinder corners, with a shadow so they sit on the tile.
    $pw = [single][Math]::Max(1.6, $s * 0.062)
    if ($detail) {
        $dark = New-Object System.Drawing.Pen (Argb 90 0 0 0), $pw
        $dark.StartCap = 'Round'; $dark.EndCap = 'Round'; $dark.LineJoin = 'Round'
        Corners $g $dark $s ([single]($s * 0.014))
    }
    $pen = New-Object System.Drawing.Pen ((Vert ($s * 0.2) ($s * 0.8) (Argb 255 248 248 250) (Argb 255 190 190 195))), $pw
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    Corners $g $pen $s 0

    # Raised round button in the middle.
    $cx = [single]($s / 2); $cy = [single]($s / 2); $br = [single]($s * 0.125)
    if ($detail) {
        foreach ($i in 3, 2, 1) {
            $gr = [single]($br + $s * 0.008 * $i)
            $g.FillEllipse((New-Object System.Drawing.SolidBrush (Argb 30 0 0 0)), [single]($cx - $gr), [single]($cy - $gr + $s * 0.012), [single](2 * $gr), [single](2 * $gr))
        }
    }
    $g.FillEllipse((Vert ($cy - $br) ($cy + $br) (Argb 255 244 244 246) (Argb 255 150 150 155)), [single]($cx - $br), [single]($cy - $br), [single](2 * $br), [single](2 * $br))
    if ($detail) {
        $g.DrawEllipse((New-Object System.Drawing.Pen (Argb 70 0 0 0), ([single][Math]::Max(0.75, $s / 128))), [single]($cx - $br), [single]($cy - $br), [single](2 * $br), [single](2 * $br))
        $hr = [single]($br * 0.62)
        $g.FillEllipse((Vert ($cy - $br * 0.92) ($cy + $br * 0.1) (Argb 170 255 255 255) (Argb 0 255 255 255)), [single]($cx - $hr), [single]($cy - $br * 0.88), [single](2 * $hr), [single]($br * 1.0))
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s
    $m = New-Object System.IO.MemoryStream
    $bmp.Save($m, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $m.ToArray()
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
$w.Dispose()

New-Item -ItemType Directory -Force (Split-Path $Png) | Out-Null
[System.IO.File]::WriteAllBytes($Png, $pngs[$sizes.Count - 1])
"Wrote $Out and $Png"
