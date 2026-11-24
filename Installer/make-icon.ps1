Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path (Split-Path -Parent $PSScriptRoot) "Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir | Out-Null }

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # Tile reto autentico do Windows 10 (#0078D7 -> #005A9E)
    $pad = [math]::Max(0, [int]($size * 0.03))
    $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($size - 2 * $pad), ($size - 2 * $pad))

    $p1 = New-Object System.Drawing.Point($rect.X, $rect.Y)
    $p2 = New-Object System.Drawing.Point($rect.Right, $rect.Bottom)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $p1, $p2,
        [System.Drawing.Color]::FromArgb(255, 0, 120, 215),
        [System.Drawing.Color]::FromArgb(255, 0, 78, 152))
    $g.FillRectangle($brush, $rect)

    # Borda sutil de Revelar (Win10 tile stroke)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(55, 255, 255, 255), 1)
    $g.DrawRectangle($pen, $rect.X, $rect.Y, $rect.Width - 1, $rect.Height - 1)

    # Monograma "K"
    $fontSize = [float]($size * 0.54)
    $font = New-Object System.Drawing.Font("Segoe UI Semibold", $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $textRect = New-Object System.Drawing.RectangleF(0, [float]($size * -0.02), $size, $size)
    $g.DrawString("K", $font, [System.Drawing.Brushes]::White, $textRect, $fmt)

    $pen.Dispose()
    $brush.Dispose()
    $font.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $script:lastPng = $ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = New-Object System.Collections.ArrayList
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    Get-PngBytes $bmp
    [void]$pngs.Add($script:lastPng)
    if ($s -eq 64) { $bmp.Save((Join-Path $assetsDir "KlyraOptimizer.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# Build .ico container with PNG payloads
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]$sizes.Count)   # image count

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len)
    $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
$icoPath = Join-Path $assetsDir "KlyraOptimizer.ico"
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose()

"ico gerado: " + (Get-Item $icoPath).Length + " bytes"
