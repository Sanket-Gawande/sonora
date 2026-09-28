# Regenerates Assets\Sonora.ico: the Sonora wave mark on a graphite tile, PNG-encoded at every
# size Windows asks for. Run only when the mark changes; the .ico is committed.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = @()
foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 24.0
    $r = [Math]::Max(2, 6 * $s)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $tile.AddArc($size - 2 * $r - 0.01, 0, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($size - 2 * $r - 0.01, $size - 2 * $r - 0.01, 2 * $r, 2 * $r, 0, 90)
    $tile.AddArc(0, $size - 2 * $r - 0.01, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 14, 14, 16))), $tile)
    $width = [Math]::Max(1.6, 2.4 * $s)
    $front = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 163, 184, 255), $width)
    $back = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(130, 163, 184, 255), $width)
    foreach ($pen in $front, $back) { $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round }
    function P($x, $y) { New-Object System.Drawing.PointF(([single]($x * $s)), ([single]($y * $s))) }
    # Two sound waves, the rear one fainter, inset so they read at 16 px.
    $g.DrawBezier($back, (P 4.5 10), (P 7.3 6.4), (P 9.2 6.4), (P 12 10))
    $g.DrawBezier($back, (P 12 10), (P 14.8 13.6), (P 16.7 13.6), (P 19.5 10))
    $g.DrawBezier($front, (P 4.5 15.5), (P 7.3 10.2), (P 9.2 10.2), (P 12 15.5))
    $g.DrawBezier($front, (P 12 15.5), (P 14.8 20.8), (P 16.7 20.8), (P 19.5 15.5))
    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $images += , @($size, $stream.ToArray())
}
$out = Join-Path $PSScriptRoot '..\Assets\Sonora.ico'
$writer = New-Object System.IO.BinaryWriter([System.IO.File]::Create($out))
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dim = if ($image[0] -ge 256) { 0 } else { $image[0] }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$image[1].Length); $writer.Write([uint32]$offset)
    $offset += $image[1].Length
}
foreach ($image in $images) { $writer.Write($image[1]) }
$writer.Close()
Write-Output ('Wrote ' + (Resolve-Path $out))
