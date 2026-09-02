#Requires -Version 7
<#
.SYNOPSIS
    Runs the packaged-release gate on the golden desktop.

.DESCRIPTION
    Builds the release package from this worktree, installs it into a clean
    directory, and proves the published binaries start, connect, and drive the
    key journeys:

      1. golden-renderer environment gate;
      2. Invoke-ReleaseSmoke.ps1 against the installed package, with the
         packaged-Watch process-independence check included;
      3. the MesIngest.Tests regression, with every skip named and matched
         against an explicit user approval;
      4. Invoke-WatchAcceptance.ps1 driving the packaged Watch through the
         non-pixel suites;
      5. a second environment gate, to prove the desktop the run started on is
         the desktop it finished on.

    It does not repeat the pixel candidates, stability counts, baseline
    promotions, or DPI clone that the visual gate already accepted for this UI
    output. Only packaging that actually changes PNG/XML/UIA/DPI output
    invalidates those, and only they are then rerun. See
    docs/agents/golden-renderer.md.

    This script must run ON the golden desktop, in an interactive session. Until
    2026-09-02 its predecessor, Invoke-GoldenRendererValidation.ps1, ran on a
    control host and reached the desktop over PowerShell Direct: it staged a
    payload, opened a PSSession to gpt_win11, registered an Interactive
    scheduled task, polled it, copied evidence back and tore the task down. The
    golden desktop is now a CI runner in the same session, so all of that
    machinery described a network hop that no longer exists. What remains is the
    gate itself.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Ticket,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(96, 144)]
    [int]$ExpectedDpi = 96,

    [ValidateRange(1, 16384)]
    [int]$ExpectedDesktopWidth = 1920,

    [ValidateRange(1, 16384)]
    [int]$ExpectedDesktopHeight = 1080,

    [string]$ArtifactsDirectory,

    # A DPAPI-protected PSCredential exported by the current user. The password
    # never reaches the repository, a log, or a command line: it is read here and
    # goes straight into a connection string held in this process.
    [Parameter(Mandatory = $true)]
    [string]$SqlServerCredentialPath,

    [Parameter(Mandatory = $true)]
    [string]$SqlServerDataSource,

    [Parameter(Mandatory = $true)]
    [string]$SqlServerDatabase,

    [Parameter(Mandatory = $true)]
    [switch]$SqlServerDatabaseIsDedicatedEmpty,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 99)]
    [int]$SqlServerExpectedProductMajor,

    [Parameter(Mandatory = $true)]
    [ValidateRange(80, 999)]
    [int]$SqlServerExpectedCompatibilityLevel,

    [string]$SqlSkipApprovalPath,

    [Parameter(Mandatory = $true)]
    [string]$ManualAcceptancePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = $PSScriptRoot
$requiredFiles = @(
    'Invoke-WatchUiTests.ps1',
    'Test-GoldenRendererEnvironment.ps1',
    'MesIngest.Watch.UiTests\MesIngest.Watch.UiTests.csproj',
    'MesIngest.Tests\MesIngest.Tests.csproj',
    'pack\Publish-MesIngest.ps1',
    'pack\Test-ReleasePackage.ps1',
    'pack\validation\Invoke-ReleaseSmoke.ps1',
    'pack\validation\Invoke-WatchAcceptance.ps1'
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) {
        throw "Packaged release gate source is missing: $required"
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path $source ".artifacts\packaged-release\ticket-$Ticket\run-$stamp"
} else {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
}
if (Test-Path -LiteralPath $artifacts) {
    throw "ArtifactsDirectory must not already exist: $artifacts"
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$logPath = Join-Path $artifacts 'release-gate.log'

# The install and package trees are byte-for-byte evidence, so they live beside
# the rest of the run rather than in a temporary directory that a failure would
# leave behind unlabelled.
$packagePayload = Join-Path $artifacts 'ReleasePackage\MesIngest'
$installRoot = Join-Path $artifacts 'CleanInstall\MesIngest'

if (-not $SqlServerDatabaseIsDedicatedEmpty) {
    throw 'The SQL Server target must be explicitly confirmed dedicated, disposable, and empty.'
}

# Without a real instance and its expected identity the whole V2 projection suite
# reports NotExecuted, and the release would claim a SQL Server gate it never ran.
$credentialSource = (Resolve-Path -LiteralPath $SqlServerCredentialPath -ErrorAction Stop).Path
$sqlServerCredential = Import-Clixml -LiteralPath $credentialSource
if ($sqlServerCredential -isnot [PSCredential] `
        -or [string]::IsNullOrWhiteSpace($sqlServerCredential.UserName)) {
    throw 'SqlServerCredentialPath must contain a DPAPI-protected PSCredential exported by the current user.'
}

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $SqlServerDataSource
$builder['Initial Catalog'] = $SqlServerDatabase
$builder['User ID'] = $sqlServerCredential.UserName
$builder['Password'] = $sqlServerCredential.GetNetworkCredential().Password
$builder['Encrypt'] = $true
$builder['TrustServerCertificate'] = $true
$builder['Persist Security Info'] = $false
$builder['Connect Timeout'] = 15
$sqlConnectionString = $builder.ConnectionString

$manualSource = (Resolve-Path -LiteralPath $ManualAcceptancePath -ErrorAction Stop).Path
$manual = Get-Content -Raw -LiteralPath $manualSource | ConvertFrom-Json
$requiredChecks = @(
    'startup-within-25-seconds',
    'demand-alert-visual-contract',
    'v2-pages-and-settings',
    'package-run-not-source'
)
$manualChecks = @($manual.confirmedChecks | Sort-Object -Unique)
if ([string]::IsNullOrWhiteSpace([string]$manual.approvedBy) `
        -or [string]::IsNullOrWhiteSpace([string]$manual.approvedAt) `
        -or [string]::IsNullOrWhiteSpace([string]$manual.userMessage) `
        -or @(Compare-Object -ReferenceObject $requiredChecks -DifferenceObject $manualChecks).Count -gt 0) {
    throw 'Manual acceptance must identify the approver/message and confirm all four packaged-release checks.'
}
$manualAcceptanceInfo = [ordered]@{
    ApprovedBy = [string]$manual.approvedBy
    ApprovedAt = [string]$manual.approvedAt
    Sha256 = (Get-FileHash -LiteralPath $manualSource -Algorithm SHA256).Hash
}

$approval = $null
$sqlSkipApprovalInfo = $null
if (-not [string]::IsNullOrWhiteSpace($SqlSkipApprovalPath)) {
    $approvalSource = (Resolve-Path -LiteralPath $SqlSkipApprovalPath -ErrorAction Stop).Path
    $approval = Get-Content -Raw -LiteralPath $approvalSource | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$approval.approvedBy) `
            -or [string]::IsNullOrWhiteSpace([string]$approval.approvedAt) `
            -or [string]::IsNullOrWhiteSpace([string]$approval.userMessage) `
            -or @($approval.tests).Count -eq 0) {
        throw 'SQL skip approval must contain approvedBy, approvedAt, userMessage, and the exact approved test names.'
    }
    $sqlSkipApprovalInfo = [ordered]@{
        ApprovedBy = [string]$approval.approvedBy
        ApprovedAt = [string]$approval.approvedAt
        TestCount = @($approval.tests).Count
        Sha256 = (Get-FileHash -LiteralPath $approvalSource -Algorithm SHA256).Hash
    }
}

$gitCommit = (@(& git -C $source rev-parse HEAD 2>$null) | Select-Object -First 1)
$gitStatus = @(
    if ($LASTEXITCODE -eq 0) {
        & git -C $source status --porcelain=v1 --untracked-files=normal -- . `
            ':(exclude).artifacts/**' `
            ':(exclude)MesIngest.Tests/TestResults/**' 2>$null
    }
)

[ordered]@{
    Ticket = $Ticket
    Suite = 'watch-package-release'
    Configuration = $Configuration
    CreatedAt = [DateTimeOffset]::Now.ToString('O')
    Machine = [Environment]::MachineName
    SourceDirectory = $source
    GitCommit = $gitCommit
    GitDirty = $gitStatus.Count -gt 0
    GitStatus = $gitStatus
    ExpectedEnvironment = [ordered]@{
        DesktopWidth = $ExpectedDesktopWidth
        DesktopHeight = $ExpectedDesktopHeight
        Dpi = $ExpectedDpi
    }
    SqlSkipApproval = $sqlSkipApprovalInfo
    ManualAcceptance = $manualAcceptanceInfo
    SqlServer = [ordered]@{
        Configured = $true
        DataSource = $SqlServerDataSource
        Database = $SqlServerDatabase
        Authentication = 'SqlPasswordFromDpapiCredential'
        DedicatedEmptyConfirmed = $true
        ExpectedProductMajor = $SqlServerExpectedProductMajor
        ExpectedCompatibilityLevel = $SqlServerExpectedCompatibilityLevel
    }
} | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $artifacts 'run-manifest.json') -Encoding utf8

function Invoke-GoldenEnvironmentGate {
    param([Parameter(Mandatory = $true)][string]$OutputPath)

    & (Join-Path $source 'Test-GoldenRendererEnvironment.ps1') `
        -ExpectedDpi $ExpectedDpi `
        -ExpectedDesktopWidth $ExpectedDesktopWidth `
        -ExpectedDesktopHeight $ExpectedDesktopHeight `
        -OutputPath $OutputPath 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Golden environment gate failed with exit code $LASTEXITCODE; see $OutputPath."
    }
}

function Assert-NoResidualProcesses {
    $residual = @(Get-Process -Name 'MesIngest.Host', 'MesIngest.Watch', 'testhost', 'vstest.console' `
            -ErrorAction SilentlyContinue)
    if ($residual.Count -gt 0) {
        throw "Residual test processes remain: $($residual.ProcessName -join ', ')"
    }
}

$env:NUGET_XMLDOC_MODE = 'skip'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:MesIngestWatch__RenderingMode = 'SoftwareOnly'
$env:MES_INGEST_RELEASE_SMOKE_SQLSERVER = $sqlConnectionString
$env:MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED = 'YES'
$env:MES_INGEST_SQLSERVER = $sqlConnectionString
# The V2 projection suite reads its own variable and creates per-test databases.
# Without these three the whole suite reports NotExecuted.
$env:MES_INGEST_TICKET01_SQLSERVER = $sqlConnectionString
$env:MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR = $SqlServerExpectedProductMajor
$env:MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL = $SqlServerExpectedCompatibilityLevel

try {
    Invoke-GoldenEnvironmentGate -OutputPath (Join-Path $artifacts 'environment.json')

    & (Join-Path $source 'pack\Publish-MesIngest.ps1') `
        -OutputDir $packagePayload `
        -Configuration $Configuration `
        -Runtime 'win-x64' 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Release package build failed with exit code $LASTEXITCODE."
    }

    # A clean install directory, so the gate exercises the published layout rather
    # than the build output it was produced from.
    New-Item -ItemType Directory -Path (Split-Path -Parent $installRoot) -Force | Out-Null
    Copy-Item -LiteralPath $packagePayload -Destination $installRoot -Recurse

    # This runs on the interactive golden desktop, so the packaged Watch process
    # independence check is executed rather than skipped.
    & (Join-Path $installRoot 'validation\Invoke-ReleaseSmoke.ps1') `
        -IncludePackagedWatch `
        -ArtifactsDirectory (Join-Path $artifacts 'release-smoke') 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Release smoke failed with exit code $LASTEXITCODE."
    }

    $regressionDirectory = Join-Path $artifacts 'core-host-http-sql'
    New-Item -ItemType Directory -Path $regressionDirectory -Force | Out-Null
    $regressionProject = Join-Path $source 'MesIngest.Tests\MesIngest.Tests.csproj'
    & dotnet restore $regressionProject --ignore-failed-sources -p:NuGetAudit=false 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Regression restore failed with exit code $LASTEXITCODE."
    }
    # The guest is zh-CN and prints 已通过! rather than Passed!, so the exit code and
    # the TRX are the contract; never match on the console text.
    & dotnet test $regressionProject `
        --configuration $Configuration `
        --no-restore `
        --results-directory $regressionDirectory `
        --logger 'trx;LogFileName=core-host-http-sql.trx' 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Regression suite failed with exit code $LASTEXITCODE."
    }

    [xml]$trx = Get-Content -Raw -LiteralPath (Join-Path $regressionDirectory 'core-host-http-sql.trx')
    $allResults = @($trx.TestRun.Results.UnitTestResult)
    $skippedResults = @($allResults | Where-Object { $_.outcome -eq 'NotExecuted' })
    $unexpectedSkips = @($skippedResults | Where-Object {
            $_.testName -notmatch '(?:SqlServer|SchemaUpgrade)'
        })
    $approvedTests = if ($null -ne $approval) { @($approval.tests | Sort-Object -Unique) } else { @() }
    $actualSkippedTests = @($skippedResults | ForEach-Object { $_.testName } | Sort-Object -Unique)
    $approvalDifferences = @(
        @($actualSkippedTests | Where-Object { $_ -notin $approvedTests })
        @($approvedTests | Where-Object { $_ -notin $actualSkippedTests })
    )
    [ordered]@{
        total = [int]$trx.TestRun.ResultSummary.Counters.total
        executed = [int]$trx.TestRun.ResultSummary.Counters.executed
        passed = [int]$trx.TestRun.ResultSummary.Counters.passed
        failed = [int]$trx.TestRun.ResultSummary.Counters.failed
        skipped = $skippedResults.Count
        skippedTests = $actualSkippedTests
        approval = $approval
        approvalMatchesEverySkippedTest = $approvalDifferences.Count -eq 0
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $regressionDirectory 'summary.json') -Encoding utf8
    if ($unexpectedSkips.Count -gt 0) {
        throw "Unexpected non-SQL regression skips: $($unexpectedSkips.testName -join ', ')"
    }
    if ($skippedResults.Count -gt 0 -and ($null -eq $approval -or $approvalDifferences.Count -gt 0)) {
        throw ("SQL_SERVER_SKIPS_REQUIRE_EXACT_USER_APPROVAL: $($skippedResults.Count) named skips " +
            "must exactly match SqlSkipApprovalPath; details are in $regressionDirectory\summary.json")
    }

    # A packaged release proves the published binaries start, connect, and drive
    # the key journeys. It does not repeat the pixel candidates, the stability
    # runs, the baseline promotions, or the DPI clone the visual gate already
    # accepted for this UI output.
    $acceptanceDirectory = Join-Path $artifacts 'packaged-watch-acceptance'
    & (Join-Path $installRoot 'validation\Invoke-WatchAcceptance.ps1') `
        -HarnessRoot $source `
        -Suite watch-production-preview `
        -Configuration $Configuration `
        -ArtifactsDirectory $acceptanceDirectory 2>&1 |
        Tee-Object -FilePath $logPath -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged Watch acceptance failed with exit code $LASTEXITCODE."
    }

    $uiRunnerLogs = @(Get-ChildItem -LiteralPath $acceptanceDirectory -Recurse -Filter '*.runner.log' -File)
    $expectedPackagedSuites = @('watch-ui-journeys', 'watch-vm-tests')
    $actualPackagedSuites = @(
        $uiRunnerLogs | ForEach-Object { $_.BaseName.Replace('.runner', '') } | Sort-Object
    )
    if (@(Compare-Object `
                -ReferenceObject $expectedPackagedSuites `
                -DifferenceObject $actualPackagedSuites `
                -CaseSensitive).Count -gt 0) {
        throw ("Packaged Watch acceptance must run exactly the non-pixel suites " +
            "$($expectedPackagedSuites -join ', '); found $($actualPackagedSuites -join ', ').")
    }

    # Two UI entry points can never run inside a release payload, by construction:
    # the candidate comparison is the stability gate's own entry point, and the
    # golden-fixture equivalence tests read real captures from .artifacts, which the
    # payload deliberately excludes. They are named here rather than tolerated as a
    # count, so any other skip still fails the gate.
    $expectedUiSkips = @(
        'MesIngest.Watch.UiTests.WatchWindowCandidateEquivalenceTests.Candidate_directories_are_visually_equivalent',
        'MesIngest.Watch.UiTests.WatchWindowVisualEquivalenceGoldenFixtureTests.A_different_page_is_rejected',
        'MesIngest.Watch.UiTests.WatchWindowVisualEquivalenceGoldenFixtureTests.A_one_pixel_control_geometry_change_is_rejected',
        'MesIngest.Watch.UiTests.WatchWindowVisualEquivalenceGoldenFixtureTests.A_one_pixel_glyph_shift_is_rejected',
        'MesIngest.Watch.UiTests.WatchWindowVisualEquivalenceGoldenFixtureTests.The_recorded_cross_deployment_area_text_raster_flip_is_accepted',
        'MesIngest.Watch.UiTests.WatchWindowVisualEquivalenceGoldenFixtureTests.The_recorded_antialiasing_flip_is_accepted'
    )
    $actualUiSkips = @(
        $uiRunnerLogs |
            Select-String -Pattern '^\s+(?<test>\S+) \[SKIP\]$' |
            ForEach-Object { $_.Matches[0].Groups['test'].Value } |
            Sort-Object -Unique
    )
    $uiSkipDifference = @(
        Compare-Object `
            -ReferenceObject $expectedUiSkips `
            -DifferenceObject $actualUiSkips `
            -CaseSensitive
    )
    [ordered]@{
        suites = $actualPackagedSuites
        runnerLogCount = $uiRunnerLogs.Count
        skippedTests = $actualUiSkips
        skippedReason = 'STABILITY_GATE_ENTRY_POINT_AND_GOLDEN_FIXTURES_EXCLUDED_FROM_RELEASE_PAYLOAD'
        skippedMatchesExpectedNamedSet = $uiSkipDifference.Count -eq 0
        visualBaselinesReused = $true
        visualReuseBasis = 'PACKAGING_DID_NOT_CHANGE_APPROVED_PNG_XML_UIA_OR_DPI_OUTPUT'
    } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $acceptanceDirectory 'summary.json') -Encoding utf8
    if ($uiSkipDifference.Count -gt 0) {
        throw ('PACKAGED_WATCH_UI_SKIPS_NOT_ALLOWED: the skipped UI tests are not the ' +
            "expected named set; details are in $acceptanceDirectory\summary.json")
    }

    Assert-NoResidualProcesses
    Invoke-GoldenEnvironmentGate -OutputPath (Join-Path $artifacts 'environment-post.json')

    $releaseEvidence = Join-Path $artifacts 'release-package'
    New-Item -ItemType Directory -Path $releaseEvidence -Force | Out-Null
    $packageManifestPath = Join-Path $packagePayload 'RELEASE-MANIFEST.json'
    Copy-Item -LiteralPath (Join-Path $packagePayload 'VERSION.txt') -Destination $releaseEvidence
    Copy-Item -LiteralPath $packageManifestPath -Destination $releaseEvidence
    [ordered]@{
        schemaVersion = 1
        completedAt = [DateTimeOffset]::Now.ToString('O')
        machine = [Environment]::MachineName
        packageManifestSha256 = (Get-FileHash -LiteralPath $packageManifestPath -Algorithm SHA256).Hash
        packageManifest = Get-Content -Raw -LiteralPath $packageManifestPath | ConvertFrom-Json
        preEnvironment = 'environment.json'
        postEnvironment = 'environment-post.json'
        releaseSmoke = Get-Content -Raw -LiteralPath (
            Join-Path $artifacts 'release-smoke\release-smoke-result.json') | ConvertFrom-Json
        coreHostHttpSql = Get-Content -Raw -LiteralPath (
            Join-Path $regressionDirectory 'summary.json') | ConvertFrom-Json
        packagedWatchAcceptance = Get-Content -Raw -LiteralPath (
            Join-Path $acceptanceDirectory 'summary.json') | ConvertFrom-Json
        manualAcceptance = Get-Content -Raw -LiteralPath $manualSource | ConvertFrom-Json
    } | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath (Join-Path $releaseEvidence 'RELEASE-SIGNOFF.json') -Encoding utf8
    Compress-Archive -Path (Join-Path $packagePayload '*') `
        -DestinationPath (Join-Path $releaseEvidence 'MesIngest-win-x64.zip') `
        -CompressionLevel Fastest

    [ordered]@{
        Status = 'PASSED'
        CompletedAt = [DateTimeOffset]::Now.ToString('O')
        ArtifactsDirectory = $artifacts
    } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $artifacts 'result.json') -Encoding utf8
    Write-Output "PACKAGED_RELEASE_GATE_PASSED: ticket=$Ticket artifacts=$artifacts"
}
catch {
    [ordered]@{
        Status = 'FAILED'
        CompletedAt = [DateTimeOffset]::Now.ToString('O')
        Error = $_.Exception.ToString()
        ArtifactsDirectory = $artifacts
    } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $artifacts 'result.json') -Encoding utf8
    [Console]::Error.WriteLine("PACKAGED_RELEASE_GATE_FAILED: ticket=$Ticket artifacts=$artifacts")
    throw
}
finally {
    # The connection string carries the SQL password. It exists only in this
    # process, and it does not outlive it.
    foreach ($name in @(
            'MES_INGEST_RELEASE_SMOKE_SQLSERVER',
            'MES_INGEST_SQLSERVER',
            'MES_INGEST_TICKET01_SQLSERVER')) {
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    Get-Process -Name 'MesIngest.Host', 'MesIngest.Watch', 'testhost', 'vstest.console' `
        -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
