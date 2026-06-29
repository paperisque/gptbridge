<#
.SYNOPSIS
  Конвертер иконки: PNG (источник) → installer\app.ico с набором размеров.
  Источник по умолчанию — poc-webview2\icons\ico2.png (рисовал юзер).
  Кадры пакуем PNG-кодированными (Win10/11 это понимает), качественный ресайз.
  Готовый app.ico установщик подхватывает сам (tools\build-installer.ps1 → /DAppIcon).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = "$PSScriptRoot\..\poc-webview2\icons\ico2.png",
    [string]$Out    = "$PSScriptRoot\..\installer\app.ico",
    [int[]]$Sizes   = @(256, 128, 64, 48, 32, 24, 16)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Источник не найден: $Source" }

function Resize-Square([System.Drawing.Image]$src, [int]$S) {
    $bmp = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CompositingMode    = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $attr = New-Object System.Drawing.Imaging.ImageAttributes
    $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)  # без ореола по краям
    $dst = New-Object System.Drawing.Rectangle(0, 0, $S, $S)
    $g.DrawImage($src, $dst, 0, 0, $src.Width, $src.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
    $g.Dispose(); $attr.Dispose()
    return $bmp
}

# ---- собрать ICO (PNG-кодированные кадры) ------------------------------------
function Save-Ico([System.Drawing.Bitmap[]]$bitmaps, [string]$path) {
    $pngs = @()
    foreach ($bm in $bitmaps) {
        $msp = New-Object System.IO.MemoryStream
        $bm.Save($msp, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , ($msp.ToArray())
        $msp.Dispose()
    }
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$bitmaps.Count)
    $offset = 6 + 16 * $bitmaps.Count
    for ($i = 0; $i -lt $bitmaps.Count; $i++) {
        $bm = $bitmaps[$i]; $data = $pngs[$i]
        $wB = if ($bm.Width  -ge 256) { 0 } else { $bm.Width }
        $hB = if ($bm.Height -ge 256) { 0 } else { $bm.Height }
        $bw.Write([Byte]$wB); $bw.Write([Byte]$hB)
        $bw.Write([Byte]0);   $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    foreach ($data in $pngs) { $bw.Write($data) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
}

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    $bitmaps = @()
    foreach ($s in $Sizes) { $bitmaps += (Resize-Square $src $s) }
    Save-Ico $bitmaps $Out
    foreach ($b in $bitmaps) { $b.Dispose() }
}
finally { $src.Dispose() }

Write-Output ("ICO: {0}  ({1} размеров: {2})" -f [System.IO.Path]::GetFullPath($Out), $Sizes.Count, ($Sizes -join ','))
