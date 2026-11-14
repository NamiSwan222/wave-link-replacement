# Draws the WaveFix icon (white mic on an indigo rounded tile) and writes a multi-size .ico
# with PNG-compressed entries. Usage: make-icon.ps1 -Out src\WaveFix\Assets\WaveFix.ico
param([string]$Out = (Join-Path $PSScriptRoot '..\src\WaveFix\Assets\WaveFix.ico'))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  $k = $s / 256.0
  # Rounded tile with a soft indigo gradient
  $r = 56 * $k; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($s-2*$r, 0, 2*$r, 2*$r, 270, 90)
  $path.AddArc($s-2*$r, $s-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $s-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $s,$s), ([System.Drawing.Color]::FromArgb(0x81,0x8C,0xF8)), ([System.Drawing.Color]::FromArgb(0x4F,0x46,0xE5))
  $g.FillPath($grad, $path)
  # Mic capsule
  $white = [System.Drawing.Brushes]::White
  $cw = 64*$k; $ch = 104*$k; $cx = ($s-$cw)/2; $cy = 44*$k
  $cap = New-Object System.Drawing.Drawing2D.GraphicsPath
  $cap.AddArc($cx, $cy, $cw, $cw, 180, 180); $cap.AddArc($cx, $cy+$ch-$cw, $cw, $cw, 0, 180); $cap.CloseFigure()
  $g.FillPath($white, $cap)
  # Stand: arc, stem, base
  $pw = [Math]::Max(1.5, 14*$k)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
  $g.DrawArc($pen, [single](76*$k), [single](92*$k), [single](104*$k), [single](88*$k), 0, 180)
  $g.DrawLine($pen, [single](128*$k), [single](180*$k), [single](128*$k), [single](210*$k))
  if ($s -ge 32) { $g.DrawLine($pen, [single](100*$k), [single](212*$k), [single](156*$k), [single](212*$k)) }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  ,$ms.ToArray()
}
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $d = $pngs[$i]
  $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
  $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$d.Length); $w.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes, sizes $($sizes -join ','))"
# Preview PNG of the largest size next to the icon
[System.IO.File]::WriteAllBytes([System.IO.Path]::ChangeExtension($Out, '.preview.png'), $pngs[-1])
