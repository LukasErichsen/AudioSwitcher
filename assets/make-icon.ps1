<#
.SYNOPSIS
    Generates assets/app.ico containing 16x16, 24x24, 32x32, 48x48, 64x64, 128x128, and 256x256 frames.
#>
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngEntries = @()

foreach ($size in $sizes) {
    $grid = New-Object System.Windows.Controls.Grid
    $grid.Width = $size
    $grid.Height = $size

    $corner = $size * 0.25
    $border = New-Object System.Windows.Controls.Border
    $border.CornerRadius = New-Object System.Windows.CornerRadius($corner)
    $border.Background = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#E4EFF9')
    $border.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Stretch
    $border.VerticalAlignment = [System.Windows.VerticalAlignment]::Stretch

    $text = New-Object System.Windows.Controls.TextBlock
    $text.Text = [char]0xE995
    $text.FontFamily = New-Object System.Windows.Media.FontFamily('Segoe Fluent Icons, Segoe MDL2 Assets')
    $text.Foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#005FB8')
    $text.FontSize = $size * 0.50
    $text.HorizontalAlignment = [System.Windows.HorizontalAlignment]::Center
    $text.VerticalAlignment = [System.Windows.VerticalAlignment]::Center

    $border.Child = $text
    $grid.Children.Add($border) | Out-Null

    $grid.Measure([System.Windows.Size]::new($size, $size))
    $grid.Arrange([System.Windows.Rect]::new(0, 0, $size, $size))
    $grid.UpdateLayout()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($grid)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    $bytes = $ms.ToArray()

    $pngEntries += [PSCustomObject]@{
        Size = $size
        Data = $bytes
    }
}

$icoPath = Join-Path $PSScriptRoot "app.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR (6 bytes)
$bw.Write([uint16]0) # Reserved
$bw.Write([uint16]1) # Type 1 = Icon
$bw.Write([uint16]$pngEntries.Count) # Count

$offset = 6 + (16 * $pngEntries.Count)

# ICONDIRENTRY array (16 bytes each)
foreach ($entry in $pngEntries) {
    $w = if ($entry.Size -eq 256) { 0 } else { [byte]$entry.Size }
    $h = if ($entry.Size -eq 256) { 0 } else { [byte]$entry.Size }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0) # Palette colors
    $bw.Write([byte]0) # Reserved
    $bw.Write([uint16]1) # Color planes
    $bw.Write([uint16]32) # Bits per pixel
    $bw.Write([uint32]$entry.Data.Length)
    $bw.Write([uint32]$offset)
    $offset += $entry.Data.Length
}

# Image Data
foreach ($entry in $pngEntries) {
    $bw.Write($entry.Data)
}

$bw.Flush()
$fs.Dispose()

Write-Host "Generated $icoPath with $($pngEntries.Count) icon frames."

