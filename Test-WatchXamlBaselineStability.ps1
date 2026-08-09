[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateRange(10, 100)]
    [int]$Runs = 10,

    [string]$DiagnosticsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runner = Join-Path $PSScriptRoot "Invoke-WatchUiTests.ps1"
$baselineDirectory = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\Baselines\SelectedUi"
$manifests = [System.Collections.Generic.List[string]]::new()
$manifestMaps = [System.Collections.Generic.List[hashtable]]::new()
$firstMismatchRun = $null

Add-Type -AssemblyName PresentationCore

function Assert-PngDimensions {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][int]$Run
    )

    foreach ($file in $Files) {
        $viewport = [regex]::Match(
            $file.Name,
            '-(?<width>\d+)x(?<height>\d+)\.(received|verified)\.png$')
        if (-not $viewport.Success) {
            throw "PNG baseline name does not declare its viewport: $($file.Name)"
        }

        $stream = [System.IO.File]::OpenRead($file.FullName)
        try {
            $decoder = [System.Windows.Media.Imaging.PngBitmapDecoder]::new(
                $stream,
                [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,
                [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
            $frame = $decoder.Frames[0]
            $expectedWidth = [int]$viewport.Groups['width'].Value
            $expectedHeight = [int]$viewport.Groups['height'].Value
            if ($frame.PixelWidth -ne $expectedWidth -or $frame.PixelHeight -ne $expectedHeight) {
                throw "PNG viewport mismatch on run $Run for $($file.Name): expected ${expectedWidth}x${expectedHeight}, actual $($frame.PixelWidth)x$($frame.PixelHeight)."
            }
        } finally {
            $stream.Dispose()
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($DiagnosticsDirectory)) {
    New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force | Out-Null
    if (Get-ChildItem -LiteralPath $DiagnosticsDirectory -Force | Select-Object -First 1) {
        throw "DiagnosticsDirectory must be empty: $DiagnosticsDirectory"
    }
}

for ($run = 1; $run -le $Runs; $run++) {
    if (Test-Path -LiteralPath $baselineDirectory -PathType Container) {
        Get-ChildItem -LiteralPath $baselineDirectory -Filter "*.received.*" -File |
            Remove-Item -Force
    }

    & $runner -Configuration $Configuration -Suite watch-xaml-visual
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -eq 2) {
        exit 2
    }
    if ($testExitCode -notin @(0, 1)) {
        throw "watch-xaml-visual exited with unexpected code $testExitCode on run $run."
    }

    $received = @(
        Get-ChildItem -LiteralPath $baselineDirectory -Filter "*.received.*" -File |
            Sort-Object Name
    )
    $pngForDimensionCheck = if ($received.Count -eq 0) {
        @(Get-ChildItem -LiteralPath $baselineDirectory -Filter "*.verified.png" -File)
    } else {
        @($received | Where-Object Extension -eq ".png")
    }
    Assert-PngDimensions -Files $pngForDimensionCheck -Run $run
    if ($received.Count -eq 0) {
        if ($testExitCode -ne 0) {
            throw "Run $run failed without producing received artifacts."
        }
        $manifests.Add("VERIFIED")
    } else {
        $manifestMap = @{}
        $manifest = $received | ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            $manifestMap[$_.Name] = $hash
            "$($_.Name)=$hash"
        }
        $manifests.Add(($manifest -join "`n"))
        $manifestMaps.Add($manifestMap)

        $isFirstRun = $run -eq 1
        $isFirstMismatch = -not $isFirstRun `
            -and $null -eq $firstMismatchRun `
            -and $manifests[$run - 1] -ne $manifests[0]
        if ($isFirstMismatch) {
            $firstMismatchRun = $run
        }
        if (-not [string]::IsNullOrWhiteSpace($DiagnosticsDirectory) `
            -and ($isFirstRun -or $isFirstMismatch)) {
            $label = if ($isFirstRun) { "run-1" } else { "run-$run-first-mismatch" }
            $runDirectory = Join-Path $DiagnosticsDirectory $label
            New-Item -ItemType Directory -Path $runDirectory | Out-Null
            $received | Copy-Item -Destination $runDirectory
        }
    }

    Write-Host "WATCH_XAML_STABILITY_RUN: $run/$Runs"
}

$first = $manifests[0]
$unstableRun = 1..($manifests.Count - 1) |
    Where-Object { $manifests[$_] -ne $first } |
    Select-Object -First 1
if ($null -ne $unstableRun) {
    $differentNames = @(
        $manifestMaps[0].Keys |
            Where-Object {
                -not $manifestMaps[$unstableRun].ContainsKey($_) `
                    -or $manifestMaps[$unstableRun][$_] -ne $manifestMaps[0][$_]
            } |
            Sort-Object
    )
    $differenceSummary = if ($differentNames.Count -eq 0) {
        "manifest membership differs"
    } else {
        $differentNames -join ", "
    }
    throw "Visual artifacts are not deterministic: run 1 differs from run $($unstableRun + 1). Files: $differenceSummary"
}

if ($first -eq "VERIFIED") {
    Write-Host "WATCH_XAML_STABLE: all approved baselines matched in $Runs consecutive runs."
} else {
    Write-Host "WATCH_XAML_CANDIDATE_STABLE: received XAML/PNG were byte-identical in $Runs consecutive runs."
    Write-Host "Review before/after/diff and the environment report; this script deliberately does not accept or rename baselines."
}

exit 0
