#Requires -Version 7
Set-StrictMode -Version Latest

function Read-WatchPngPixels {
    param([Parameter(Mandatory = $true)][string]$Path)

    Add-Type -AssemblyName PresentationCore
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
            $stream,
            [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
            [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
            $decoder.Frames[0],
            [System.Windows.Media.PixelFormats]::Bgra32,
            $null,
            0)
        $stride = $converted.PixelWidth * 4
        $pixels = [byte[]]::new($stride * $converted.PixelHeight)
        $converted.CopyPixels($pixels, $stride, 0)
        return [pscustomobject]@{
            Width = $converted.PixelWidth
            Height = $converted.PixelHeight
            Stride = $stride
            Pixels = $pixels
        }
    } finally {
        $stream.Dispose()
    }
}

function Assert-WatchPngDimensions {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height
    )

    $image = Read-WatchPngPixels -Path $Path
    if ($image.Width -ne $Width -or $image.Height -ne $Height) {
        throw "Expected PNG dimensions ${Width}x${Height}; actual=$($image.Width)x$($image.Height): $Path"
    }
}

function Write-WatchPngDiff {
    param(
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Actual,
        [Parameter(Mandatory = $true)][string]$Output
    )

    $expectedImage = Read-WatchPngPixels -Path $Expected
    $actualImage = Read-WatchPngPixels -Path $Actual
    if ($expectedImage.Width -ne $actualImage.Width -or $expectedImage.Height -ne $actualImage.Height) {
        throw "Expected and actual PNG dimensions differ."
    }

    $diff = [byte[]]::new($expectedImage.Pixels.Length)
    for ($index = 0; $index -lt $diff.Length; $index += 4) {
        $same = $expectedImage.Pixels[$index] -eq $actualImage.Pixels[$index] `
            -and $expectedImage.Pixels[$index + 1] -eq $actualImage.Pixels[$index + 1] `
            -and $expectedImage.Pixels[$index + 2] -eq $actualImage.Pixels[$index + 2] `
            -and $expectedImage.Pixels[$index + 3] -eq $actualImage.Pixels[$index + 3]
        if ($same) {
            $diff[$index] = 245
            $diff[$index + 1] = 245
            $diff[$index + 2] = 245
        } else {
            $diff[$index] = 255
            $diff[$index + 1] = 0
            $diff[$index + 2] = 255
        }
        $diff[$index + 3] = 255
    }

    $bitmap = [System.Windows.Media.Imaging.BitmapSource]::Create(
        $expectedImage.Width,
        $expectedImage.Height,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Bgra32,
        $null,
        $diff,
        $expectedImage.Stride)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.File]::Create($Output)
    try {
        $encoder.Save($stream)
    } finally {
        $stream.Dispose()
    }
}
