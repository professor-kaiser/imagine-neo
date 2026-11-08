param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $ProjectRoot 'ui\Assets'
$svgPath = Join-Path $assetDirectory 'imagine-neo-logo.svg'
$converter = Join-Path $ProjectRoot '.tools\miniforge3\envs\photolab\Library\bin\rsvg-convert.exe'
if (!(Test-Path -LiteralPath $converter)) {
    throw 'rsvg-convert is required. It is included in the local photolab Conda environment.'
}
# The SVG is the only drawing source. Render large, then export antialiased app/icon sizes.
$renderPath = Join-Path ([System.IO.Path]::GetTempPath()) ('imagine-neo-brand-' + [guid]::NewGuid().ToString('N') + '.png')
& $converter --width 2048 --height 2048 --output $renderPath $svgPath
if ($LASTEXITCODE -ne 0) { throw 'SVG rendering failed.' }
$bitmap = [System.Drawing.Bitmap]::FromFile($renderPath)
try {
    $iconImages = @()
    foreach ($size in @(16, 24, 32, 48, 64, 128, 256, 512, 1024)) {
        $scaled = New-Object System.Drawing.Bitmap $size, $size
        $drawing = [System.Drawing.Graphics]::FromImage($scaled)
        $drawing.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $drawing.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $drawing.DrawImage($bitmap, 0, 0, $size, $size)
        $stream = New-Object System.IO.MemoryStream
        $scaled.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $stream.ToArray()
        if ($size -le 256) { $iconImages += [PSCustomObject]@{ Size = $size; Bytes = $bytes } }
        if ($size -eq 512) { [System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'imagine-neo-logo.png'), $bytes) }
        if ($size -eq 1024) { [System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'imagine-neo-logo-1024.png'), $bytes) }
        $stream.Dispose()
        $drawing.Dispose()
        $scaled.Dispose()
    }
    $iconStream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $iconStream
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$iconImages.Count)
    $offset = 6 + 16 * $iconImages.Count
    foreach ($entry in $iconImages) {
        $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $iconImages) { $writer.Write([byte[]]$entry.Bytes) }
    $writer.Flush()
    [System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'imagine-neo.ico'), $iconStream.ToArray())
    $writer.Dispose()
    $iconStream.Dispose()
} finally {
    $bitmap.Dispose()
    Remove-Item -LiteralPath $renderPath
}
Write-Output "Brand assets created from $svgPath"

