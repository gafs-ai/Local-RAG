param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$brand = Join-Path (Split-Path $PSScriptRoot -Parent) 'Assets/Brand'
$source = [Drawing.Bitmap]::new((Join-Path $brand 'local-rag-logo.png'))
try {
    # Normalize transparent margins without stretching or changing the approved mark.
    $left = $source.Width; $top = $source.Height; $right = -1; $bottom = -1
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            if ($source.GetPixel($x, $y).A -ge 32) {
                $left = [Math]::Min($left, $x); $right = [Math]::Max($right, $x)
                $top = [Math]::Min($top, $y); $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    if ($right -lt $left) { throw 'Logo has no visible pixels.' }
    $crop = [Drawing.RectangleF]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
    $frames = @()
    foreach ($size in @(16,20,24,32,40,48,64,128,256,512)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = $size * 0.90 / [Math]::Max($crop.Width, $crop.Height)
            $width = $crop.Width * $scale; $height = $crop.Height * $scale
            $destination = [Drawing.RectangleF]::new(($size - $width) / 2, ($size - $height) / 2, $width, $height)
            $graphics.DrawImage($source, $destination, $crop, [Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $bytes = $stream.ToArray()
            if ($size -eq 512 -or $size -eq 128) {
                [IO.File]::WriteAllBytes((Join-Path $brand "local-rag-$size.png"), $bytes)
            }
            if ($size -le 256) { $frames += @{Size=$size;Bytes=$bytes} }
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $brand 'local-rag.ico'))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose() }
} finally { $source.Dispose() }
Write-Output 'Generated square PNGs (128, 512) and Windows ICO (16–256).'
