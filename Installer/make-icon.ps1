param(
    [string]$SourcePng = ""
)

Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path (Split-Path -Parent $PSScriptRoot) "Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir | Out-Null }

$targetPng = Join-Path $assetsDir "KlyraOptimizer.png"
$icoPath = Join-Path $assetsDir "KlyraOptimizer.ico"

if ($SourcePng -and (Test-Path $SourcePng)) {
    $raw = [System.Drawing.Bitmap]::new($SourcePng)

    # Recorta a area util dos pixels nao-transparentes para maximizar nitidez em 16x16 / 24x24 / 32x32
    $minX = $raw.Width; $minY = $raw.Height; $maxX = 0; $maxY = 0
    for ($y = 0; $y -lt $raw.Height; $y++) {
        for ($x = 0; $x -lt $raw.Width; $x++) {
            if ($raw.GetPixel($x, $y).A -gt 8) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }

    $cropW = [math]::Max(1, $maxX - $minX + 1)
    $cropH = [math]::Max(1, $maxY - $minY + 1)
    $cropSide = [math]::Max($cropW, $cropH)
    $pad = [int]([math]::Round($cropSide * 0.04))
    $canvasSide = $cropSide + (2 * $pad)

    $master = New-Object System.Drawing.Bitmap(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gm = [System.Drawing.Graphics]::FromImage($master)
    $gm.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gm.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gm.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gm.Clear([System.Drawing.Color]::Transparent)

    $scale = 512.0 / $canvasSide
    $drawW = [float]($cropW * $scale)
    $drawH = [float]($cropH * $scale)
    $drawX = [float]((512.0 - $drawW) * 0.5)
    $drawY = [float]((512.0 - $drawH) * 0.5)

    $srcRect = New-Object System.Drawing.Rectangle($minX, $minY, $cropW, $cropH)
    $dstRect = New-Object System.Drawing.RectangleF($drawX, $drawY, $drawW, $drawH)
    $gm.DrawImage($raw, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
    $gm.Dispose()
    $raw.Dispose()

    $master.Save($targetPng, [System.Drawing.Imaging.ImageFormat]::Png)
}
elseif (Test-Path $targetPng) {
    $master = [System.Drawing.Bitmap]::new($targetPng)
}
else {
    throw "KlyraOptimizer.png nao encontrado em $assetsDir."
}

function Resize-IconFrame([System.Drawing.Bitmap]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = New-Object System.Collections.ArrayList

foreach ($s in $sizes) {
    $frame = Resize-IconFrame $master $s
    $ms = New-Object System.IO.MemoryStream
    $frame.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$pngs.Add($ms.ToArray())
    $ms.Dispose()
    $frame.Dispose()
}

$master.Dispose()

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
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
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$bw.Dispose()

Write-Host ("Icone oficial gerado: PNG=" + (Get-Item $targetPng).Length + " bytes, ICO=" + (Get-Item $icoPath).Length + " bytes")
