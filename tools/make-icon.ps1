# Draws the app icon (rounded square with sound-wave bars) and writes a multi-size .ico.
# Usage: powershell -File tools\make-icon.ps1 -Out src\Soundboard\Soundboard.ico
param([string]$Out = "$PSScriptRoot\..\src\Soundboard\Soundboard.ico")

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded-square background
    $r = [Math]::Max(2, [int]($size * 0.22))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($size - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($size - $r - 1, $size - $r - 1, $r, $r, 0, 90)
    $path.AddArc(0, $size - $r - 1, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $size), ([System.Drawing.Color]::FromArgb(255, 0x4A, 0x61, 0xB0)), ([System.Drawing.Color]::FromArgb(255, 0x2C, 0x3B, 0x6E))
    $g.FillPath($bg, $path)

    # Five sound-wave bars, tallest in the middle, with a warm accent on the centre bar
    $heights = 0.30, 0.55, 0.78, 0.55, 0.30
    $barW = $size * 0.11
    $gap = $size * 0.055
    $total = 5 * $barW + 4 * $gap
    $x = ($size - $total) / 2
    for ($i = 0; $i -lt 5; $i++) {
        $h = $size * $heights[$i]
        $y = ($size - $h) / 2
        $color = if ($i -eq 2) { [System.Drawing.Color]::FromArgb(255, 0xF2, 0xB8, 0x4B) } else { [System.Drawing.Color]::White }
        $brush = New-Object System.Drawing.SolidBrush $color
        $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
        $br = [Math]::Max(1, $barW)
        $bar.AddArc($x, $y, $br, $br, 180, 180)
        $bar.AddArc($x, $y + $h - $br, $br, $br, 0, 180)
        $bar.CloseFigure()
        $g.FillPath($brush, $bar)
        $x += $barW + $gap
    }

    $g.Dispose()
    return $bmp
}

# The 256 px frame is stored as PNG. Smaller frames use the classic 32-bit DIB format,
# which every icon reader understands: a BITMAPINFOHEADER, bottom-up BGRA rows, then an
# all-zero AND mask (transparency comes from the alpha channel).
function ConvertTo-IconFrame([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$ms.ToArray()
    }

    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([UInt32]40); $w.Write([Int32]$size); $w.Write([Int32]($size * 2))   # height covers XOR + AND
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]0)
    $w.Write([UInt32]($size * $size * 4)); $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $data.Stride, $size * 4) }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = New-IconPng $s
    $images.Add([byte[]](ConvertTo-IconFrame $bmp))
    $bmp.Dispose()
}

# ICO file: ICONDIR header, one ICONDIRENTRY per size, then the PNG data for each.
$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | ForEach-Object { Join-Path $_ (Split-Path $Out -Leaf) }))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Dispose()
Write-Host "Wrote $Out"
