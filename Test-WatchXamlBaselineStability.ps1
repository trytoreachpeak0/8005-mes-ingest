[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateRange(1, 100)]
    [int]$Runs = 3,

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

    # Build once. This loop looks for runtime nondeterminism in the renderer, so every
    # iteration must execute the same binaries; rebuilding between them exercises the
    # compiler instead.
    #
    # Where the time actually goes, measured on gpt_win11 (run-20260819-105020, three
    # iterations): restore 30 s on iteration 1 and skipped after, xUnit reports 38.6 s of
    # tests, and the test process then sits for 5.6-6.0 min between writing its last
    # capture and returning. That gap is ~80% of the ~7.3 min iteration and is not build,
    # not restore, and not `dotnet run` evaluation - all three were measured and are
    # seconds. The STA threads are IsBackground with a 30 s cap, so they are not holding
    # it either. Unresolved; it deserves its own ticket rather than a guess here.
    # Until then the effective lever is -Runs: 3 costs ~22 min where 10 cost ~77.
    if ($run -eq 1) {
        & $runner -Configuration $Configuration -Suite watch-xaml-visual
    } else {
        & $runner -Configuration $Configuration -Suite watch-xaml-visual -ReuseBuild
    }

    $testExitCode = $LASTEXITCODE
    if ($testExitCode -eq 2) {
        exit 2
    }
    if ($testExitCode -eq 5) {
        throw "Run $run could not reuse the $Configuration build; run 1 must produce it."
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
