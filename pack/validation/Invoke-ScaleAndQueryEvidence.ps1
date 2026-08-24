#Requires -Version 5.1
<#
.SYNOPSIS
  Build an owned MesIngest scale database and capture fail-closed query/storage evidence.

.DESCRIPTION
  This destructive validation tool is intentionally separate from the daily Host. It creates only
  a new, explicitly named MesIngest_Scale_* database on a real SQL Server master connection,
  marks ownership with a database extended property, drives the packaged production Host through
  its V2 HTTP API, captures actual plans and statement metrics with Extended Events, writes an
  immutable evidence bundle, and removes only the database whose ownership marker matches this run.

  The 0/7/30 profiles use the calibrated 14-second round cadence and 600 observations per round.
  Profile 0 retains one current round but no historical rounds. Profiles 7 and 30 retain the same
  current state and add the exact equivalent historical round count.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(0, 7, 30)]
    [int] $ProfileDays,

    [Parameter(Mandatory = $true)]
    [string] $DatabaseName,

    [Parameter(Mandatory = $true)]
    [string] $ConfirmIsolatedDatabase,

    [string] $ServiceRoot = '',
    [string] $OutputRoot = '',
    [string] $SqlConnectionStringEnvironmentVariable = 'MES_INGEST_SCALE_EVIDENCE_SQLSERVER',
    [string] $SqlTier1AttestationPath = '',
    [string] $DeterministicContractEvidencePath = '',
    [string] $CapacityBlockerEvidencePath = '',
    [string] $BaselineEvidencePath = '',
    [string] $ValidateEvidenceFixturePath = '',
    [string] $ValidateCapacityFixturePath = '',
    [string] $ValidateStabilityFixturePath = '',
    [string] $ValidatePercentileFixture = '',
    [string] $ValidateShowPlanFixturePath = '',
    [ValidateSet(
        'All',
        'DemandSeries',
        'ExternallyReadableDemandCatalog',
        'CurrentIngestAttention',
        'Overview',
        'ReadabilityAudit',
        'ErrorSearch',
        'PollTrace',
        'RawEvidence')]
    [string] $QuerySurface = 'All',
    [string] $ConfirmFullScaleEscalation = '',
    [ValidateSet('Release', 'Debug', 'Published')] [string] $BuildConfiguration = 'Release',
    [ValidateRange(1, 10000)] [int] $SeriesCount = 600,
    [ValidateRange(1, 10000)] [int] $ObservationsPerRound = 600,
    [ValidateRange(1, 3600)] [int] $RoundIntervalSeconds = 14,
    [ValidateRange(1, 10000)] [int] $RoundBatchSize = 250,
    [ValidateRange(0, 100000)] [long] $RepresentativeHistoryRounds = 0,
    [switch] $FastCapacityProjection,
    [switch] $AcceleratedConcurrencyStability,
    [ValidateRange(30, 45)] [int] $StabilityDurationMinutes = 30,
    [ValidateRange(1, 10)] [int] $AcceleratedPollStartIntervalSeconds = 1,
    [ValidateRange(1, 300)] [int] $AcceleratedCleanupCheckIntervalSeconds = 60,
    [ValidateRange(1, 100)] [int] $WarmupCount = 2,
    [ValidateRange(1, 1000)] [int] $MeasurementCount = 5,
    [ValidateRange(1, 600)] [int] $RequestTimeoutSeconds = 120,
    [DateTimeOffset] $AnchorUtc = [DateTimeOffset]::Parse('2026-08-01T00:00:00Z'),
    [switch] $KeepDatabase
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$requiredConfirmation = 'MESINGEST_SCALE_EVIDENCE_ONLY'
$databasePrefix = 'MesIngest_Scale_'
$systemDatabases = @('master', 'model', 'msdb', 'tempdb')

# Validate destructive identity before reading a connection string, contacting SQL, or creating output.
if ($ConfirmIsolatedDatabase -cne $requiredConfirmation) {
    throw "ConfirmIsolatedDatabase must equal $requiredConfirmation."
}
if ($systemDatabases -contains $DatabaseName -or
    $DatabaseName -notmatch '^MesIngest_Scale_[A-Za-z0-9_]{1,64}$' -or
    [string]::Equals($DatabaseName, 'MesIngest_V2', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unsafe scale database name '$DatabaseName'. Use a new $databasePrefix name."
}
if ($RepresentativeHistoryRounds -gt 0 -and $ProfileDays -ne 0) {
    throw 'RepresentativeHistoryRounds is available only with ProfileDays 0.'
}
if ($RepresentativeHistoryRounds -gt 0 -and [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
    throw 'Representative history evidence requires BaselineEvidencePath from a passing empty run.'
}
if ($RepresentativeHistoryRounds -eq 0 -and -not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
    throw 'BaselineEvidencePath is valid only for representative history evidence.'
}
if ($ProfileDays -in @(7, 30) -and
    $ConfirmFullScaleEscalation -cne 'MESINGEST_FULL_SCALE_ESCALATION') {
    throw 'ProfileDays 7/30 requires ConfirmFullScaleEscalation=MESINGEST_FULL_SCALE_ESCALATION.'
}
if ($FastCapacityProjection -and $ProfileDays -ne 0) {
    throw 'FastCapacityProjection is available only with ProfileDays 0.'
}
if ($FastCapacityProjection -and
    (($RepresentativeHistoryRounds * [long]$ObservationsPerRound) + [long]$SeriesCount) -gt 250000) {
    throw 'FastCapacityProjection refuses to materialize more than 250,000 RawObservation rows.'
}
if ($AcceleratedConcurrencyStability -and $ProfileDays -ne 0) {
    throw 'AcceleratedConcurrencyStability is available only with ProfileDays 0.'
}
if ($AcceleratedConcurrencyStability -and $FastCapacityProjection) {
    throw 'AcceleratedConcurrencyStability cannot be combined with FastCapacityProjection.'
}
if ($AcceleratedConcurrencyStability -and
    (($RepresentativeHistoryRounds * [long]$ObservationsPerRound) + [long]$SeriesCount) -gt 250000) {
    throw 'AcceleratedConcurrencyStability refuses to materialize more than 250,000 RawObservation rows.'
}

$profiles = @{
    0 = [ordered]@{ historyDays = 0; distribution = 'production-calibrated'; activeRatio = 0.70; archivedRatio = 0.30; errorRatio = 0.10 }
    7 = [ordered]@{ historyDays = 7; distribution = 'production-calibrated'; activeRatio = 0.70; archivedRatio = 0.30; errorRatio = 0.10 }
    30 = [ordered]@{ historyDays = 30; distribution = 'production-calibrated'; activeRatio = 0.70; archivedRatio = 0.30; errorRatio = 0.10 }
}
$profile = $profiles[$ProfileDays]
$canonicalScaleProfile = $SeriesCount -eq 600 -and
    $ObservationsPerRound -eq 600 -and
    $RoundIntervalSeconds -eq 14
$historyRoundCount = if ($RepresentativeHistoryRounds -gt 0) {
    $RepresentativeHistoryRounds
} elseif ($ProfileDays -eq 0) { 0L } else {
    [long][Math]::Floor(($ProfileDays * 86400.0) / $RoundIntervalSeconds)
}
$historyObservationCount = $historyRoundCount * [long]$ObservationsPerRound
$expectedTotalObservationCount = $historyObservationCount + [long]$SeriesCount

if ([string]::IsNullOrWhiteSpace($ServiceRoot)) {
    $ServiceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'service'
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'scale-evidence-runs'
}

Add-Type -AssemblyName System.Data
Add-Type -AssemblyName System.Net.Http

function Get-StreamSha256 {
    param([Parameter(Mandatory = $true)][IO.Stream] $Stream)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string] $Path)
    $stream = [IO.File]::OpenRead($Path)
    try { return Get-StreamSha256 $stream } finally { $stream.Dispose() }
}

function Get-Sha256String {
    param([Parameter(Mandatory = $true)][string] $Value)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-NearestRankPercentile {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][double[]] $Values,
        [Parameter(Mandatory = $true)][ValidateRange(0.0, 1.0)][double] $Percentile
    )
    if ($Values.Count -eq 0) { throw 'A percentile requires at least one sample.' }
    $sorted = @($Values | Sort-Object)
    $rank = [Math]::Ceiling($Percentile * $sorted.Count)
    $index = [Math]::Max(0, [int]$rank - 1)
    return [double]$sorted[$index]
}

function Get-LongPropertySum {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Items,
        [Parameter(Mandatory = $true)][string] $Property
    )
    $total = 0L
    foreach ($item in @($Items)) {
        if ($null -ne $item -and $null -ne $item.$Property) {
            $total += [long]$item.$Property
        }
    }
    return $total
}

function Get-LongPropertyMaximum {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Items,
        [Parameter(Mandatory = $true)][string] $Property
    )
    $maximum = 0L
    foreach ($item in @($Items)) {
        if ($null -ne $item -and $null -ne $item.$Property) {
            $maximum = [Math]::Max($maximum, [long]$item.$Property)
        }
    }
    return $maximum
}

function Get-FastCapacityProjection {
    param([Parameter(Mandatory = $true)][object] $Evidence)

    $failures = New-Object System.Collections.ArrayList
    $sample = $Evidence.sample
    $baseline = $Evidence.baseline
    $environment = $Evidence.environment
    $cleanup = $Evidence.cleanup
    $historyRows = [long]$sample.historyObservationCount
    $historyRounds = [long]$sample.historyRoundCount
    $totalRows = [long]$sample.rawObservationCount
    $roundIntervalSeconds = [int]$sample.roundIntervalSeconds
    $observationsPerRound = [int]$sample.observationsPerRound
    $targetRounds = [long][Math]::Floor((30.0 * 86400.0) / $roundIntervalSeconds)
    $targetRows = $targetRounds * [long]$observationsPerRound
    $safetyMargin = 0.30

    if ($totalRows -gt 250000) { [void]$failures.Add('CAPACITY_SAMPLE_EXCEEDS_250000') }
    if ($historyRows -lt 180000 -or $historyRounds -lt 300) {
        [void]$failures.Add('CAPACITY_SAMPLE_INSUFFICIENT')
    }
    if ($historyRows -ne ($historyRounds * [long]$observationsPerRound)) {
        [void]$failures.Add('CAPACITY_SAMPLE_DISTRIBUTION_UNCERTAIN')
    }
    if ($totalRows -ne ($historyRows + 600L)) {
        [void]$failures.Add('CAPACITY_SAMPLE_DISTRIBUTION_UNCERTAIN')
    }

    $checkpoints = @($sample.checkpoints | Sort-Object { [long]$_.historyRoundCount })
    $segmentRates = New-Object System.Collections.ArrayList
    for ($index = 1; $index -lt $checkpoints.Count; $index++) {
        $roundDelta = [long]$checkpoints[$index].historyRoundCount - [long]$checkpoints[$index - 1].historyRoundCount
        $usedDelta = [double]$checkpoints[$index].logicalUsedMb - [double]$checkpoints[$index - 1].logicalUsedMb
        if ($roundDelta -le 0 -or $usedDelta -le 0) {
            [void]$failures.Add('CAPACITY_GROWTH_NONLINEAR')
            continue
        }
        [void]$segmentRates.Add($usedDelta / $roundDelta)
    }
    $linearityRatio = $null
    if ($checkpoints.Count -lt 4 -or $segmentRates.Count -lt 3) {
        [void]$failures.Add('CAPACITY_GROWTH_EVIDENCE_INSUFFICIENT')
    } else {
        $minimumRate = [double](($segmentRates | Measure-Object -Minimum).Minimum)
        $maximumRate = [double](($segmentRates | Measure-Object -Maximum).Maximum)
        $linearityRatio = if ($minimumRate -gt 0) { $maximumRate / $minimumRate } else { [double]::PositiveInfinity }
        if ($linearityRatio -gt 1.20) { [void]$failures.Add('CAPACITY_GROWTH_NONLINEAR') }
    }

    $baselineLogicalMb = [double]$baseline.storage.logicalUsedMb
    $sampleLogicalMb = [double]$sample.storage.logicalUsedMb
    $logicalGrowthMb = $sampleLogicalMb - $baselineLogicalMb
    if ($logicalGrowthMb -le 0 -or $historyRounds -le 0) {
        [void]$failures.Add('CAPACITY_LOGICAL_GROWTH_UNCERTAIN')
        $logicalPerRoundMb = 0.0
    } else {
        $logicalPerRoundMb = $logicalGrowthMb / $historyRounds
    }
    $logicalPerRowMb = if ($historyRows -gt 0) { $logicalGrowthMb / $historyRows } else { 0.0 }
    $coreProjectedLogicalUsedMb = ($baselineLogicalMb + ($logicalPerRoundMb * $targetRounds)) * (1.0 + $safetyMargin)

    $requiredTransientProperties = @(
        'tombstoneObservedCount', 'tombstoneLogicalUsedMb', 'projectedTombstoneCount',
        'databaseVersionStorePeakMb', 'tempdbVersionStorePeakMb',
        'tempdbUserObjectsImpactMb', 'tempdbInternalObjectsImpactMb')
    $transientEvidenceComplete = @($requiredTransientProperties | Where-Object {
        $null -eq $sample.PSObject.Properties[$_]
    }).Count -eq 0
    if (-not $transientEvidenceComplete) {
        [void]$failures.Add('CAPACITY_TRANSIENT_STORAGE_EVIDENCE_INCOMPLETE')
        $projectedTombstoneMb = 0.0
        $projectedVersionStorePeakMb = 0.0
        $projectedTempdbImpactMb = 0.0
    } else {
        $tombstoneObservedCount = [long]$sample.tombstoneObservedCount
        if ($tombstoneObservedCount -le 0 -or [double]$sample.tombstoneLogicalUsedMb -le 0 -or
            [long]$sample.projectedTombstoneCount -lt $tombstoneObservedCount) {
            [void]$failures.Add('CAPACITY_TOMBSTONE_PROJECTION_UNCERTAIN')
            $projectedTombstoneMb = 0.0
        } else {
            $projectedTombstoneMb = (([double]$sample.tombstoneLogicalUsedMb / $tombstoneObservedCount) *
                [long]$sample.projectedTombstoneCount) * (1.0 + $safetyMargin)
        }
        $projectedVersionStorePeakMb = [double]$sample.databaseVersionStorePeakMb * (1.0 + $safetyMargin)
        $tempdbMeasuredImpactMb = [Math]::Max(
            [double]$sample.tempdbVersionStorePeakMb,
            [Math]::Max(0.0, [double]$sample.tempdbUserObjectsImpactMb) +
                [Math]::Max(0.0, [double]$sample.tempdbInternalObjectsImpactMb))
        $projectedTempdbImpactMb = $tempdbMeasuredImpactMb * (1.0 + $safetyMargin)
    }
    $projectedLogicalUsedMb = $coreProjectedLogicalUsedMb + $projectedTombstoneMb

    $dataFileGrowthMb = [double]$sample.dataFileGrowthMb
    if ($dataFileGrowthMb -le 0) {
        [void]$failures.Add('CAPACITY_AUTOGROWTH_UNCERTAIN')
        $dataFileGrowthMb = 1.0
    }
    $startingPhysicalDataMb = [double]$sample.storage.physicalDataMb
    $projectedPhysicalDataMb = if ($projectedLogicalUsedMb -le $startingPhysicalDataMb) {
        $startingPhysicalDataMb
    } else {
        $startingPhysicalDataMb +
            ([Math]::Ceiling(($projectedLogicalUsedMb - $startingPhysicalDataMb) / $dataFileGrowthMb) * $dataFileGrowthMb)
    }
    $peakPhysicalLogMb = if ($null -eq $sample.PSObject.Properties['peakPhysicalLogMb']) {
        [double]$sample.storage.ldfMb
    } else {
        [double]$sample.peakPhysicalLogMb
    }
    $peakLogMb = [Math]::Max(
        [Math]::Max([double]$sample.storage.ldfMb, $peakPhysicalLogMb),
        [double]$sample.peakLogUsedMb)
    $projectedLdfMb = $peakLogMb * (1.0 + $safetyMargin)

    $logicalLimitMb = 12.0 * 1024.0
    $physicalLimitMb = 16.0 * 1024.0
    $ldfLimitMb = 2.0 * 1024.0
    $escalationFraction = 0.70
    if ($projectedLogicalUsedMb -ge ($logicalLimitMb * $escalationFraction)) {
        [void]$failures.Add('CAPACITY_LOGICAL_70_PERCENT_ESCALATION')
    }
    if ($projectedPhysicalDataMb -ge ($physicalLimitMb * $escalationFraction)) {
        [void]$failures.Add('CAPACITY_PHYSICAL_70_PERCENT_ESCALATION')
    }
    if ($projectedLdfMb -ge ($ldfLimitMb * $escalationFraction)) {
        [void]$failures.Add('CAPACITY_LDF_70_PERCENT_ESCALATION')
    }

    if ([string]$environment.recoveryModel -cne 'SIMPLE') { [void]$failures.Add('CAPACITY_RECOVERY_NOT_SIMPLE') }
    if ([int]$environment.compatibilityLevel -ne 160) { [void]$failures.Add('CAPACITY_COMPATIBILITY_NOT_160') }
    if ([int]$environment.maxServerMemoryMb -ne 1536) { [void]$failures.Add('CAPACITY_MAX_SERVER_MEMORY_NOT_1536') }
    if (-not [bool]$environment.pageCompressionVerified) { [void]$failures.Add('CAPACITY_PAGE_COMPRESSION_UNCERTAIN') }
    if (-not [bool]$environment.autoGrowthVerified) { [void]$failures.Add('CAPACITY_AUTOGROWTH_UNCERTAIN') }
    if (-not [bool]$environment.versionStoreMeasured) { [void]$failures.Add('CAPACITY_VERSION_STORE_UNCERTAIN') }
    if (-not [bool]$environment.tempdbMeasured) { [void]$failures.Add('CAPACITY_TEMPDB_UNCERTAIN') }
    if ([string]::IsNullOrWhiteSpace([string]$environment.logReuseWait)) {
        [void]$failures.Add('CAPACITY_LOG_REUSE_WAIT_UNCERTAIN')
    }

    $requiredCleanupProperties = @(
        'expectedRawObservationRows', 'deletedRawObservationRows', 'remainingRawObservationRows',
        'expectedEligibleSeries', 'deletedEligibleSeries', 'remainingRetentionEligibleSeries',
        'tombstonesWritten', 'activeSeriesWholeBefore', 'activeSeriesWholeAfter',
        'activeGraphUnchanged', 'defaultCheckIntervalSeconds', 'defaultRawObservationBatch',
        'defaultSeriesBatch', 'defaultTimeBudgetSeconds')
    $cleanupEvidenceComplete = $null -ne $cleanup -and
        @($requiredCleanupProperties | Where-Object {
            $null -eq $cleanup.PSObject.Properties[$_]
        }).Count -eq 0
    if (-not $cleanupEvidenceComplete) {
        [void]$failures.Add('CAPACITY_CLEANUP_EVIDENCE_INCOMPLETE')
    } else {
        if ([long]$cleanup.expectedRawObservationRows -ne $totalRows -or
            ([long]$cleanup.deletedRawObservationRows + [long]$cleanup.remainingRawObservationRows) -ne
                [long]$cleanup.expectedRawObservationRows -or
            [long]$cleanup.deletedRawObservationRows -ne [long]$cleanup.expectedRawObservationRows -or
            [long]$cleanup.remainingRawObservationRows -ne 0) {
            [void]$failures.Add('CAPACITY_RAW_CLEANUP_UNCERTAIN')
        }
        if ([long]$cleanup.deletedEligibleSeries -ne [long]$cleanup.expectedEligibleSeries -or
            [long]$cleanup.tombstonesWritten -ne [long]$cleanup.expectedEligibleSeries -or
            [long]$cleanup.remainingRetentionEligibleSeries -ne 0) {
            [void]$failures.Add('CAPACITY_SERIES_CLEANUP_UNCERTAIN')
        }
        if ([long]$cleanup.activeSeriesWholeBefore -ne [long]$cleanup.activeSeriesWholeAfter -or
            -not [bool]$cleanup.activeGraphUnchanged) {
            [void]$failures.Add('CAPACITY_ACTIVE_SERIES_SPLIT')
        }
        if ([int]$cleanup.defaultCheckIntervalSeconds -ne 3600 -or
            [int]$cleanup.defaultRawObservationBatch -ne 210000 -or
            [int]$cleanup.defaultSeriesBatch -ne 25 -or
            [int]$cleanup.defaultTimeBudgetSeconds -ne 15) {
            [void]$failures.Add('CAPACITY_CLEANUP_DEFAULTS_UNCERTAIN')
        }
    }

    $uniqueFailures = @($failures | Sort-Object -Unique)
    return [pscustomobject][ordered]@{
        passed = $uniqueFailures.Count -eq 0
        escalationRequired = $uniqueFailures.Count -gt 0
        failures = $uniqueFailures
        model = [ordered]@{
            targetDays = 30; targetRounds = $targetRounds; targetRawObservationRows = $targetRows
            safetyMarginFraction = $safetyMargin; escalationFraction = $escalationFraction
            historySampleRounds = $historyRounds; historySampleRows = $historyRows; totalSampleRows = $totalRows
            observedLogicalGrowthMb = $logicalGrowthMb
            logicalMbPerRound = $logicalPerRoundMb; logicalMbPerRow = $logicalPerRowMb
            formula = '((baselineLogicalMb + logicalMbPerRound * targetRounds) * 1.30) + projectedTombstoneMb'
            tombstoneFormula = '(observedTombstoneMb / observedTombstoneCount) * projectedTombstoneCount * 1.30'
            versionStoreFormula = 'measuredDatabaseVersionStorePeakMb * 1.30'
            tempdbFormula = 'max(measuredTempdbVersionStorePeakMb, positive user + internal object impact) * 1.30'
            physicalFormula = 'startingPhysicalDataMb + ceil((projectedLogicalUsedMb - startingPhysicalDataMb) / dataFileGrowthMb) * dataFileGrowthMb'
            ldfFormula = 'max(sampleLdfMb, peakPhysicalLogMb across load/query/cleanup, peakLogUsedMb) * 1.30 under SIMPLE recovery'
            linearityMaximumToMinimumSegmentRate = $linearityRatio
            projectedTombstoneCount = if ($transientEvidenceComplete) { [long]$sample.projectedTombstoneCount } else { $null }
        }
        thresholds = [ordered]@{
            logicalUsedMb = $logicalLimitMb; physicalDataMb = $physicalLimitMb; ldfMb = $ldfLimitMb
            logicalEscalationMb = $logicalLimitMb * $escalationFraction
            physicalEscalationMb = $physicalLimitMb * $escalationFraction
            ldfEscalationMb = $ldfLimitMb * $escalationFraction
        }
        prediction = [ordered]@{
            logicalUsedMb = $projectedLogicalUsedMb
            physicalDataMb = $projectedPhysicalDataMb
            ldfMb = $projectedLdfMb
            tombstoneMb = $projectedTombstoneMb
            databaseVersionStorePeakMb = $projectedVersionStorePeakMb
            tempdbImpactMb = $projectedTempdbImpactMb
        }
    }
}

function Test-EvidenceProperties {
    param(
        [AllowNull()][object] $Value,
        [Parameter(Mandatory = $true)][string[]] $Names
    )
    if ($null -eq $Value) { return $false }
    return @($Names | Where-Object { $null -eq $Value.PSObject.Properties[$_] }).Count -eq 0
}

function Get-AcceleratedStabilityResult {
    param([Parameter(Mandatory = $true)][object] $Evidence)

    $failures = New-Object System.Collections.ArrayList
    $topLevelComplete = Test-EvidenceProperties $Evidence @(
        'identity', 'environment', 'workload', 'latency', 'resources', 'behavior', 'evidence')
    if (-not $topLevelComplete) {
        [void]$failures.Add('STABILITY_EVIDENCE_INCOMPLETE')
        return [pscustomobject][ordered]@{
            passed = $false
            soakEscalationRequired = $true
            failures = @($failures)
            nextValidation = '4-hour-or-24-hour-real-soak after evidence repair'
        }
    }

    $identity = $Evidence.identity
    $environment = $Evidence.environment
    $workload = $Evidence.workload
    $latency = $Evidence.latency
    $resources = $Evidence.resources
    $behavior = $Evidence.behavior
    $evidenceState = $Evidence.evidence

    if (-not (Test-EvidenceProperties $identity @(
            'sourceCommit', 'hostSha256', 'contractVersion', 'schemaVersion', 'historyEpoch')) -or
        [string]::IsNullOrWhiteSpace([string]$identity.sourceCommit) -or
        [string]::IsNullOrWhiteSpace([string]$identity.hostSha256) -or
        [string]::IsNullOrWhiteSpace([string]$identity.contractVersion) -or
        [int]$identity.schemaVersion -le 0 -or
        [guid]$identity.historyEpoch -eq [guid]::Empty) {
        [void]$failures.Add('STABILITY_BUILD_OR_CONTRACT_IDENTITY_INCOMPLETE')
    }

    $environmentComplete = Test-EvidenceProperties $environment @(
        'sqlProductMajor', 'compatibilityLevel', 'maxServerMemoryMb', 'recoveryModel',
        'defaultPollStartIntervalSeconds', 'failureBackoffSeconds', 'watchRefreshSeconds',
        'defaultCleanupCheckIntervalSeconds', 'acceleratedPollStartIntervalSeconds',
        'acceleratedCleanupCheckIntervalSeconds')
    if (-not $environmentComplete) {
        [void]$failures.Add('STABILITY_RELEASE_DEFAULTS_INCOMPLETE')
    } else {
        $watchDefaults = $environment.watchRefreshSeconds
        $backoffs = @($environment.failureBackoffSeconds | ForEach-Object { [int]$_ })
        if ([int]$environment.sqlProductMajor -ne 16 -or
            [int]$environment.compatibilityLevel -ne 160 -or
            [int]$environment.maxServerMemoryMb -ne 1536 -or
            [string]$environment.recoveryModel -ne 'SIMPLE') {
            [void]$failures.Add('STABILITY_LOW_MEMORY_PROFILE_MISMATCH')
        }
        if ([int]$environment.defaultPollStartIntervalSeconds -ne 60 -or
            ($backoffs -join ',') -ne '60,120,300' -or
            [int]$environment.defaultCleanupCheckIntervalSeconds -ne 3600 -or
            -not (Test-EvidenceProperties $watchDefaults @(
                'overview', 'currentAttention', 'demandSeries', 'readabilityAudit', 'errorSearch')) -or
            [int]$watchDefaults.overview -ne 30 -or
            [int]$watchDefaults.currentAttention -ne 30 -or
            [int]$watchDefaults.demandSeries -ne 60 -or
            [int]$watchDefaults.readabilityAudit -ne 60 -or
            [int]$watchDefaults.errorSearch -ne 60) {
            [void]$failures.Add('STABILITY_RELEASE_DEFAULTS_MISMATCH')
        }
        if ([int]$environment.acceleratedPollStartIntervalSeconds -ge 60 -or
            [int]$environment.acceleratedCleanupCheckIntervalSeconds -ge 3600) {
            [void]$failures.Add('STABILITY_PROFILE_NOT_ACCELERATED')
        }
    }

    $workloadComplete = Test-EvidenceProperties $workload @(
        'durationSeconds', 'seriesCount', 'initialRawObservationRows',
        'maximumRawObservationRows', 'representativeHistoryRounds', 'concurrentClients', 'operations')
    if (-not $workloadComplete) {
        [void]$failures.Add('STABILITY_WORKLOAD_EVIDENCE_INCOMPLETE')
    } else {
        if ([int]$workload.durationSeconds -lt 1800 -or [int]$workload.durationSeconds -gt 2700) {
            [void]$failures.Add('STABILITY_DURATION_OUT_OF_RANGE')
        }
        if ([int]$workload.seriesCount -ne 600 -or
            [long]$workload.initialRawObservationRows -le 0 -or
            [long]$workload.maximumRawObservationRows -lt [long]$workload.initialRawObservationRows -or
            [long]$workload.maximumRawObservationRows -gt 250000 -or
            [long]$workload.representativeHistoryRounds -le 0 -or
            [int]$workload.concurrentClients -lt 2) {
            [void]$failures.Add('STABILITY_SAMPLE_NOT_FIXED_OR_BOUNDED')
        }
        $operations = $workload.operations
        if (-not (Test-EvidenceProperties $operations @(
                'successfulPolls', 'watchApiReads', 'frozenDetailReads',
                'referenceCatalogReads', 'packagedWatchClientReads',
                'packagedReferenceConsumerReads', 'cleanupChecks', 'hostRestarts')) -or
            [long]$operations.successfulPolls -le 0 -or
            [long]$operations.watchApiReads -le 0 -or
            [long]$operations.frozenDetailReads -le 0 -or
            [long]$operations.referenceCatalogReads -le 0 -or
            [long]$operations.packagedWatchClientReads -le 0 -or
            [long]$operations.packagedReferenceConsumerReads -le 0 -or
            [long]$operations.cleanupChecks -le 0 -or
            [long]$operations.hostRestarts -ne 1) {
            [void]$failures.Add('STABILITY_OPERATION_COUNTS_INCOMPLETE')
        }
    }

    if (-not (Test-EvidenceProperties $latency @(
            'sampleCount', 'p95LatencyMs', 'p99LatencyMs',
            'firstQuartileP95LatencyMs', 'lastQuartileP95LatencyMs')) -or
        [long]$latency.sampleCount -le 0) {
        [void]$failures.Add('STABILITY_LATENCY_EVIDENCE_INCOMPLETE')
    } else {
        if ([double]$latency.p95LatencyMs -ge 2000.0) { [void]$failures.Add('STABILITY_API_P95') }
        if ([double]$latency.p99LatencyMs -ge 5000.0) { [void]$failures.Add('STABILITY_API_P99') }
        $allowedTailP95 = [Math]::Max(
            [double]$latency.firstQuartileP95LatencyMs * 2.0,
            [double]$latency.firstQuartileP95LatencyMs + 500.0)
        if ([double]$latency.lastQuartileP95LatencyMs -gt $allowedTailP95) {
            [void]$failures.Add('STABILITY_LATENCY_DEGRADATION')
        }
    }

    $resourceProperties = @(
        'snapshotCount', 'error701Count', 'resourceSemaphoreSustainedSamples', 'spillCount',
        'maximumLockWaitMs', 'unboundedLockWaitCount', 'maximumPendingMemoryGrants',
        'hostWorkingSetSlopeMbPerMinute', 'sqlWorkingSetSlopeMbPerMinute',
        'hostWorkingSetPeakMb', 'sqlWorkingSetPeakMb', 'hostHandlePeak',
        'logicalDatabaseUsedSlopeMbPerMinute', 'physicalDataFileSlopeMbPerMinute',
        'ldfSlopeMbPerMinute', 'tempdbUsedSlopeMbPerMinute', 'hostHandleSlopePerMinute',
        'databaseVersionStorePeakMb', 'tempdbVersionStorePeakMb')
    if (-not (Test-EvidenceProperties $resources $resourceProperties) -or [int]$resources.snapshotCount -lt 4) {
        [void]$failures.Add('STABILITY_RESOURCE_EVIDENCE_INCOMPLETE')
    } else {
        if ([long]$resources.error701Count -ne 0) { [void]$failures.Add('STABILITY_ERROR_701') }
        if ([long]$resources.resourceSemaphoreSustainedSamples -ne 0 -or
            [long]$resources.maximumPendingMemoryGrants -ne 0) {
            [void]$failures.Add('STABILITY_RESOURCE_SEMAPHORE')
        }
        if ([long]$resources.spillCount -ne 0) { [void]$failures.Add('STABILITY_SPILL') }
        if ([double]$resources.maximumLockWaitMs -gt 5000.0 -or
            [long]$resources.unboundedLockWaitCount -ne 0) {
            [void]$failures.Add('STABILITY_UNBOUNDED_LOCK_WAIT')
        }
        if ([double]$resources.hostWorkingSetSlopeMbPerMinute -gt 2.0) {
            [void]$failures.Add('STABILITY_HOST_MEMORY_TREND')
        }
        if ([double]$resources.sqlWorkingSetSlopeMbPerMinute -gt 8.0) {
            [void]$failures.Add('STABILITY_SQL_MEMORY_TREND')
        }
        if ([double]$resources.sqlWorkingSetPeakMb -gt 2048.0) {
            [void]$failures.Add('STABILITY_SQL_MEMORY_ENVELOPE')
        }
        if ([double]$resources.logicalDatabaseUsedSlopeMbPerMinute -gt 1.0) {
            [void]$failures.Add('STABILITY_DATABASE_USED_TREND')
        }
        if ([double]$resources.physicalDataFileSlopeMbPerMinute -gt 1.0) {
            [void]$failures.Add('STABILITY_DATABASE_FILE_TREND')
        }
        if ([double]$resources.ldfSlopeMbPerMinute -gt 1.0) {
            [void]$failures.Add('STABILITY_LDF_TREND')
        }
        if ([double]$resources.tempdbUsedSlopeMbPerMinute -gt 2.0) {
            [void]$failures.Add('STABILITY_TEMPDB_TREND')
        }
        if ([double]$resources.hostHandleSlopePerMinute -gt 1.0) {
            [void]$failures.Add('STABILITY_HANDLE_TREND')
        }
    }

    $behaviorComplete = Test-EvidenceProperties $behavior @(
        'maximumConcurrentPolls', 'catchUpBurstCount', 'currentLogicalReadGrowthPassed',
        'frozenCommitMismatchCount', 'projectionCommitsDuringFrozenReads',
        'frozenWindowsWithoutProjection', 'cleanupBacklogCount',
        'earliestAvailableAdvanced', 'storagePressurePauseCount', 'retrySchedulePassed',
        'logicalDayBoundaryPassed', 'historyEpochPreservedAcrossRestart', 'restartStatePreserved')
    if (-not $behaviorComplete) {
        [void]$failures.Add('STABILITY_BEHAVIOR_EVIDENCE_INCOMPLETE')
    } else {
        if ([int]$behavior.maximumConcurrentPolls -ne 1) { [void]$failures.Add('STABILITY_OVERLAPPING_POLL') }
        if ([long]$behavior.catchUpBurstCount -ne 0) { [void]$failures.Add('STABILITY_CATCH_UP_BURST') }
        if (-not [bool]$behavior.currentLogicalReadGrowthPassed) { [void]$failures.Add('STABILITY_CURRENT_LOGICAL_READ_GROWTH') }
        if ([long]$behavior.frozenCommitMismatchCount -ne 0) { [void]$failures.Add('STABILITY_FROZEN_COMMIT_MISMATCH') }
        if ([long]$behavior.projectionCommitsDuringFrozenReads -le 0 -or
            [long]$behavior.frozenWindowsWithoutProjection -ne 0) {
            [void]$failures.Add('STABILITY_FROZEN_READ_BLOCKED_PROJECTION')
        }
        if ([long]$behavior.cleanupBacklogCount -ne 0) { [void]$failures.Add('STABILITY_CLEANUP_BACKLOG') }
        if (-not [bool]$behavior.earliestAvailableAdvanced) { [void]$failures.Add('STABILITY_EARLIEST_AVAILABLE_NOT_ADVANCED') }
        if ([long]$behavior.storagePressurePauseCount -ne 0) { [void]$failures.Add('STABILITY_STORAGE_PRESSURE_UNEXPECTED') }
        if (-not [bool]$behavior.retrySchedulePassed) { [void]$failures.Add('STABILITY_RETRY_CONTRACT') }
        if (-not [bool]$behavior.logicalDayBoundaryPassed) { [void]$failures.Add('STABILITY_LOGICAL_DAY_BOUNDARY') }
        if (-not [bool]$behavior.historyEpochPreservedAcrossRestart -or -not [bool]$behavior.restartStatePreserved) {
            [void]$failures.Add('STABILITY_RESTART_STATE')
        }
    }

    $evidenceProperties = @(
        'resourceSnapshotsComplete', 'latencySamplesComplete', 'xeventSignalsComplete',
        'cleanupEvidenceComplete', 'deterministicContractEvidenceComplete')
    if (-not (Test-EvidenceProperties $evidenceState $evidenceProperties) -or
        @($evidenceProperties | Where-Object { -not [bool]$evidenceState.$_ }).Count -gt 0) {
        [void]$failures.Add('STABILITY_EVIDENCE_INCOMPLETE')
    }
    if ($null -ne $evidenceState.PSObject.Properties['runtimeFailureType'] -and
        -not [string]::IsNullOrWhiteSpace([string]$evidenceState.runtimeFailureType)) {
        [void]$failures.Add('STABILITY_RUNTIME_EXCEPTION')
    }

    $uniqueFailures = @($failures | Sort-Object -Unique)
    return [pscustomobject][ordered]@{
        passed = $uniqueFailures.Count -eq 0
        soakEscalationRequired = $uniqueFailures.Count -gt 0
        failures = $uniqueFailures
        thresholds = [ordered]@{
            minimumDurationSeconds = 1800; maximumDurationSeconds = 2700
            apiP95MillisecondsExclusive = 2000; apiP99MillisecondsExclusive = 5000
            maximumRawObservationRows = 250000; maximumLockWaitMilliseconds = 5000
        }
        nextValidation = if ($uniqueFailures.Count -eq 0) {
            'No long soak required unless explicitly requested before field cutover.'
        } else {
            '4-hour-or-24-hour-real-soak after resolving the named risk signal.'
        }
    }
}

function Read-XEventEnvelope {
    param([Parameter(Mandatory = $true)][string] $XmlText)
    [xml]$eventXml = $XmlText
    $values = @{}
    foreach ($data in @($eventXml.SelectNodes('/event/data'))) {
        $valueNode = $data.SelectSingleNode('value')
        if ($null -ne $valueNode) {
            $values[[string]$data.name] = if ([string]$data.name -eq 'showplan_xml') {
                [string]$valueNode.InnerXml
            } else {
                [string]$valueNode.InnerText
            }
        }
    }
    return [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Parse(
            [string]$eventXml.event.timestamp,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
        Values = $values
    }
}

function Get-XmlInt64Attribute {
    param(
        [Parameter(Mandatory = $true)][Xml.XmlElement] $Node,
        [Parameter(Mandatory = $true)][string] $Name
    )
    $value = 0L
    if ([long]::TryParse(
        $Node.GetAttribute($Name),
        [Globalization.NumberStyles]::Integer,
        [Globalization.CultureInfo]::InvariantCulture,
        [ref]$value)) {
        return $value
    }
    return 0L
}

function Read-ShowPlanRuntimeIo {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $PlanXml)
    if ([string]::IsNullOrWhiteSpace($PlanXml)) { return @() }
    [xml]$plan = $PlanXml
    $items = New-Object System.Collections.ArrayList
    foreach ($relOp in @($plan.SelectNodes("//*[local-name()='RelOp']"))) {
        $physicalOperation = $relOp.GetAttribute('PhysicalOp')
        if ($physicalOperation -notmatch '(Scan|Seek|Lookup)') { continue }
        $object = $relOp.SelectSingleNode(".//*[local-name()='Object']")
        foreach ($counter in @($relOp.SelectNodes(
            "./*[local-name()='RunTimeInformation']/*[local-name()='RunTimeCountersPerThread']"))) {
            if ($null -eq $object) { continue }
            [void]$items.Add([pscustomobject][ordered]@{
                database = $object.GetAttribute('Database'); schema = $object.GetAttribute('Schema')
                table = $object.GetAttribute('Table'); index = $object.GetAttribute('Index')
                physicalOperation = $physicalOperation; thread = $counter.GetAttribute('Thread')
                actualRows = Get-XmlInt64Attribute $counter 'ActualRows'
                actualScans = Get-XmlInt64Attribute $counter 'ActualScans'
                actualLogicalReadsPresent = $counter.HasAttribute('ActualLogicalReads')
                actualLogicalReads = Get-XmlInt64Attribute $counter 'ActualLogicalReads'
                actualPhysicalReads = Get-XmlInt64Attribute $counter 'ActualPhysicalReads'
                actualReadAheads = Get-XmlInt64Attribute $counter 'ActualReadAheads'
                actualElapsedMs = Get-XmlInt64Attribute $counter 'ActualElapsedms'
                actualCpuMs = Get-XmlInt64Attribute $counter 'ActualCPUms'
            })
        }
    }
    return @($items)
}

function Get-TotalActualLogicalReads {
    param([AllowEmptyCollection()][object[]] $RuntimeIo)
    $total = 0L
    foreach ($item in @($RuntimeIo)) {
        $total += [long]$item.actualLogicalReads
    }
    return $total
}

function Get-TotalActualScans {
    param([AllowEmptyCollection()][object[]] $RuntimeIo)
    $total = 0L
    foreach ($item in @($RuntimeIo)) {
        $total += [long]$item.actualScans
    }
    return $total
}

function Convert-DataTableRows {
    param([Parameter(Mandatory = $true)][Data.DataTable] $Table)
    $rows = New-Object System.Collections.ArrayList
    foreach ($row in $Table.Rows) {
        $values = [ordered]@{}
        foreach ($column in $Table.Columns) {
            $value = $row[$column.ColumnName]
            $values[$column.ColumnName] = if ($value -is [DBNull]) { $null } else { $value }
        }
        [void]$rows.Add([pscustomobject]$values)
    }
    return @($rows)
}

function New-SqlConnection {
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    $connection = [Data.SqlClient.SqlConnection]::new($ConnectionString)
    $connection.Open()
    return $connection
}

function Invoke-SqlNonQuery {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $CommandText,
        [hashtable] $Parameters = @{},
        [int] $CommandTimeout = 0
    )
    $connection = New-SqlConnection $ConnectionString
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandTimeout = $CommandTimeout
            $command.CommandText = $CommandText
            foreach ($entry in $Parameters.GetEnumerator()) {
                [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
            }
            return $command.ExecuteNonQuery()
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Invoke-SqlTable {
    param(
        [Parameter(Mandatory = $true)][string] $ConnectionString,
        [Parameter(Mandatory = $true)][string] $CommandText,
        [hashtable] $Parameters = @{},
        [int] $CommandTimeout = 120
    )
    $connection = New-SqlConnection $ConnectionString
    try {
        $command = $connection.CreateCommand()
        try {
            $command.CommandTimeout = $CommandTimeout
            $command.CommandText = $CommandText
            foreach ($entry in $Parameters.GetEnumerator()) {
                [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
            }
            $table = [Data.DataTable]::new()
            $reader = $command.ExecuteReader()
            try { $table.Load($reader) } finally { $reader.Dispose() }
            return @(Convert-DataTableRows $table)
        } finally { $command.Dispose() }
    } finally { $connection.Dispose() }
}

function Get-StorageSnapshot {
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    $physicalFiles = @(Invoke-SqlTable $ConnectionString @"
SELECT DB_NAME() AS database_name, name AS logical_name, type_desc, physical_name,
       size * 8.0 / 1024 AS physical_size_mb,
       FILEPROPERTY(name, 'SpaceUsed') * 8.0 / 1024 AS logical_used_mb,
       is_percent_growth,
       CASE WHEN is_percent_growth = 1 THEN CONVERT(float, growth)
            ELSE growth * 8.0 / 1024 END AS growth_value,
       CASE WHEN is_percent_growth = 1 THEN N'PERCENT' ELSE N'MB' END AS growth_unit,
       max_size
FROM sys.database_files
ORDER BY type, file_id;
"@ @{} 300)
    $allocations = @(Invoke-SqlTable $ConnectionString @"
SELECT s.name AS schema_name, t.name AS table_name, i.name AS index_name,
       CASE WHEN i.index_id = 0 THEN N'HEAP' WHEN i.index_id = 1 THEN N'CLUSTERED' ELSE N'NONCLUSTERED' END AS allocation_kind,
       p.data_compression_desc,
       SUM(ps.row_count) AS row_count,
       SUM(ps.reserved_page_count) * 8.0 / 1024 AS reserved_mb,
       SUM(ps.used_page_count) * 8.0 / 1024 AS logical_used_mb
FROM sys.dm_db_partition_stats ps
JOIN sys.partitions p ON p.partition_id = ps.partition_id
JOIN sys.tables t ON t.object_id = ps.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE s.name = N'mesingest'
GROUP BY s.name, t.name, i.name, i.index_id, p.data_compression_desc
ORDER BY t.name, i.index_id;
"@ @{} 300)
    return [pscustomobject][ordered]@{
        physicalFiles = @($physicalFiles)
        allocations = @($allocations)
        physicalDataMb = [double](($physicalFiles | Where-Object { $_.type_desc -eq 'ROWS' } | Measure-Object -Property physical_size_mb -Sum).Sum)
        ldfMb = [double](($physicalFiles | Where-Object { $_.type_desc -eq 'LOG' } | Measure-Object -Property physical_size_mb -Sum).Sum)
        logicalUsedMb = [double](($allocations | Measure-Object -Property logical_used_mb -Sum).Sum)
    }
}

function Get-CapacityResourceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $MasterConnectionString,
        [Parameter(Mandatory = $true)][string] $DatabaseConnectionString,
        [Parameter(Mandatory = $true)][string] $DatabaseName,
        [Parameter(Mandatory = $true)][string] $Stage
    )
    [void](Invoke-SqlNonQuery $DatabaseConnectionString 'CHECKPOINT;' @{} 300)
    $database = @(Invoke-SqlTable $MasterConnectionString @"
SELECT recovery_model_desc AS recoveryModel,
       log_reuse_wait_desc AS logReuseWait,
       compatibility_level AS compatibilityLevel
FROM sys.databases WHERE name = @databaseName;
"@ @{ '@databaseName' = $DatabaseName }) | Select-Object -First 1
    $log = @(Invoke-SqlTable $DatabaseConnectionString @"
SELECT total_log_size_in_bytes / 1048576.0 AS totalLogMb,
       used_log_space_in_bytes / 1048576.0 AS usedLogMb,
       used_log_space_in_percent AS usedLogPercent
FROM sys.dm_db_log_space_usage;
"@ @{}) | Select-Object -First 1
    $versionStore = @(Invoke-SqlTable $MasterConnectionString @"
SELECT reserved_page_count * 8.0 / 1024 AS versionStoreMb
FROM sys.dm_tran_version_store_space_usage
WHERE database_id = DB_ID(@databaseName);
"@ @{ '@databaseName' = $DatabaseName }) | Select-Object -First 1
    $tempdb = @(Invoke-SqlTable $MasterConnectionString @"
SELECT SUM(version_store_reserved_page_count) * 8.0 / 1024 AS versionStoreMb,
       SUM(user_object_reserved_page_count) * 8.0 / 1024 AS userObjectsMb,
       SUM(internal_object_reserved_page_count) * 8.0 / 1024 AS internalObjectsMb,
       SUM(unallocated_extent_page_count) * 8.0 / 1024 AS unallocatedMb
FROM tempdb.sys.dm_db_file_space_usage;
"@ @{}) | Select-Object -First 1
    return [pscustomobject][ordered]@{
        stage = $Stage; capturedAt = [DateTimeOffset]::UtcNow.ToString('o')
        recoveryModel = [string]$database.recoveryModel
        logReuseWait = [string]$database.logReuseWait
        compatibilityLevel = [int]$database.compatibilityLevel
        totalLogMb = [double]$log.totalLogMb; usedLogMb = [double]$log.usedLogMb
        usedLogPercent = [double]$log.usedLogPercent
        databaseVersionStoreMb = if ($null -eq $versionStore) { 0.0 } else { [double]$versionStore.versionStoreMb }
        tempdbVersionStoreMb = [double]$tempdb.versionStoreMb
        tempdbUserObjectsMb = [double]$tempdb.userObjectsMb
        tempdbInternalObjectsMb = [double]$tempdb.internalObjectsMb
        tempdbUnallocatedMb = [double]$tempdb.unallocatedMb
    }
}

function Get-ActiveSeriesGraphSnapshot {
    param([Parameter(Mandatory = $true)][string] $ConnectionString)
    $summary = @(Invoke-SqlTable $ConnectionString @"
SELECT
    (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries WHERE Lifecycle = N'TRACKING') AS seriesCount,
    (SELECT COUNT_BIG(*) FROM mesingest.TransportDemands AS demand
     INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = demand.SeriesId
     WHERE series.Lifecycle = N'TRACKING') AS demandCount,
    (SELECT COUNT_BIG(*) FROM mesingest.DemandSeriesEvents AS eventRow
     INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = eventRow.SeriesId
     WHERE series.Lifecycle = N'TRACKING') AS eventCount,
    (SELECT COUNT_BIG(*) FROM mesingest.DemandSeriesCurrentConditions AS condition
     INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = condition.SeriesId
     WHERE series.Lifecycle = N'TRACKING') AS conditionCount,
    (SELECT COUNT_BIG(*) FROM mesingest.DemandSeriesErrorPeriods AS period
     INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = period.SeriesId
     WHERE series.Lifecycle = N'TRACKING') AS errorPeriodCount,
    (SELECT COUNT_BIG(*) FROM mesingest.SeriesErrorPeriodEvidence AS evidence
     INNER JOIN mesingest.DemandSeriesErrorPeriods AS period ON period.PeriodId = evidence.PeriodId
     INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = period.SeriesId
     WHERE series.Lifecycle = N'TRACKING') AS errorEvidenceCount,
    (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries AS series
     WHERE series.Lifecycle = N'TRACKING'
       AND (series.CurrentDemandId IS NULL
            OR NOT EXISTS (SELECT 1 FROM mesingest.TransportDemands AS demand WHERE demand.DemandId = series.CurrentDemandId)
            OR NOT EXISTS (SELECT 1 FROM mesingest.DemandSeriesEvents AS eventRow WHERE eventRow.SeriesId = series.SeriesId)
            OR EXISTS
            (
                SELECT 1 FROM mesingest.DemandSeriesCurrentConditions AS condition
                WHERE condition.SeriesId = series.SeriesId
                  AND (NOT EXISTS
                       (SELECT 1 FROM mesingest.DemandSeriesErrorPeriods AS period
                        WHERE period.PeriodId = condition.PeriodId AND period.SeriesId = series.SeriesId)
                       OR NOT EXISTS
                       (SELECT 1 FROM mesingest.SeriesErrorPeriodEvidence AS evidence
                        WHERE evidence.EvidenceId = condition.LatestEvidenceId
                          AND evidence.PeriodId = condition.PeriodId))
            ))) AS splitSeriesCount;
"@ @{}) | Select-Object -First 1
    $facts = @(Invoke-SqlTable $ConnectionString @"
SELECT fact
FROM
(
    SELECT series.SeriesId, N'SERIES' AS factKind,
        CONCAT(series.SeriesId, N'|', series.KeyToken, N'|', series.WorkType, N'|', series.Sublot,
            N'|', series.Lifecycle, N'|', series.CurrentPresence, N'|', series.CurrentDemandId,
            N'|', series.LastSeriesSequence) AS fact
    FROM mesingest.DemandSeries AS series
    WHERE series.Lifecycle = N'TRACKING'
    UNION ALL
    SELECT series.SeriesId, N'DEMAND',
        CONCAT(demand.DemandId, N'|', demand.SeriesId, N'|', demand.Generation, N'|',
            COALESCE(demand.PredecessorDemandId, N''), N'|', demand.Status, N'|', demand.DemandRevision)
    FROM mesingest.TransportDemands AS demand
    INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = demand.SeriesId
    WHERE series.Lifecycle = N'TRACKING'
    UNION ALL
    SELECT series.SeriesId, N'EVENT',
        CONCAT(eventRow.EventId, N'|', eventRow.SeriesId, N'|', eventRow.SeriesSequence, N'|',
            eventRow.EventType, N'|', eventRow.SubjectKind, N'|', eventRow.SubjectId)
    FROM mesingest.DemandSeriesEvents AS eventRow
    INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = eventRow.SeriesId
    WHERE series.Lifecycle = N'TRACKING'
    UNION ALL
    SELECT series.SeriesId, N'CONDITION',
        CONCAT(condition.SeriesId, N'|', condition.ErrorCode, N'|', condition.Target, N'|',
            condition.SubjectKind, N'|', condition.PeriodId, N'|', condition.LatestEvidenceId)
    FROM mesingest.DemandSeriesCurrentConditions AS condition
    INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = condition.SeriesId
    WHERE series.Lifecycle = N'TRACKING'
    UNION ALL
    SELECT series.SeriesId, N'ERROR_PERIOD',
        CONCAT(period.PeriodId, N'|', period.SeriesId, N'|', period.ErrorCode, N'|', period.Category,
            N'|', period.Severity, N'|', period.Target, N'|', period.SubjectKind, N'|',
            CONVERT(nvarchar(40), period.StartedAt, 127), N'|', COALESCE(CONVERT(nvarchar(40), period.EndedAt, 127), N''))
    FROM mesingest.DemandSeriesErrorPeriods AS period
    INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = period.SeriesId
    WHERE series.Lifecycle = N'TRACKING'
    UNION ALL
    SELECT series.SeriesId, N'ERROR_EVIDENCE',
        CONCAT(evidence.EvidenceId, N'|', evidence.PeriodId, N'|', evidence.EventId, N'|',
            evidence.EvidenceKind, N'|', evidence.DemandId, N'|', evidence.ProjectionCommitId)
    FROM mesingest.SeriesErrorPeriodEvidence AS evidence
    INNER JOIN mesingest.DemandSeriesErrorPeriods AS period ON period.PeriodId = evidence.PeriodId
    INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = period.SeriesId
    WHERE series.Lifecycle = N'TRACKING'
) AS graphFacts
ORDER BY SeriesId, factKind, fact;
"@ @{} 300)
    $graphIdentity = Get-Sha256String (($facts | ForEach-Object { [string]$_.fact }) -join "`n")
    return [pscustomobject][ordered]@{
        seriesCount = [long]$summary.seriesCount
        demandCount = [long]$summary.demandCount
        eventCount = [long]$summary.eventCount
        conditionCount = [long]$summary.conditionCount
        errorPeriodCount = [long]$summary.errorPeriodCount
        errorEvidenceCount = [long]$summary.errorEvidenceCount
        splitSeriesCount = [long]$summary.splitSeriesCount
        graphFactCount = $facts.Count
        graphIdentitySha256 = $graphIdentity
    }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Start-EvidenceHost {
    param(
        [Parameter(Mandatory = $true)][string] $DatabaseConnectionString,
        [Parameter(Mandatory = $true)][string] $Secret,
        [Parameter(Mandatory = $true)][string] $ApplicationName,
        [int] $CleanupCheckIntervalSeconds = 0,
        [int] $PollStartIntervalSeconds = 0,
        [string] $ReplayRecordingPath = '',
        [switch] $ContinuousPoll
    )
    $hostExe = Join-Path $ServiceRoot 'MesIngest.Host.exe'
    $hostDll = Join-Path $ServiceRoot 'MesIngest.Host.dll'
    $start = [Diagnostics.ProcessStartInfo]::new()
    if (Test-Path -LiteralPath $hostExe -PathType Leaf) {
        $start.FileName = $hostExe
        $start.WorkingDirectory = $ServiceRoot
    } elseif (Test-Path -LiteralPath $hostDll -PathType Leaf) {
        $start.FileName = 'dotnet'
        $start.Arguments = '"' + $hostDll.Replace('"', '\"') + '"'
        $start.WorkingDirectory = $ServiceRoot
    } else {
        throw "Packaged Host not found under ServiceRoot: $ServiceRoot"
    }
    $port = Get-FreeTcpPort
    $baseUrl = "http://127.0.0.1:$port"
    $builder = [Data.SqlClient.SqlConnectionStringBuilder]::new($DatabaseConnectionString)
    $builder['Application Name'] = $ApplicationName
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Development'
    $start.EnvironmentVariables['MesIngest__Urls'] = $baseUrl
    $start.EnvironmentVariables['MesIngest__SnapshotSource'] = $(
        if ([string]::IsNullOrWhiteSpace($ReplayRecordingPath)) { 'None' } else { 'Oracle' })
    $start.EnvironmentVariables['MesIngest__NewSqlServerConnectionString'] = $builder.ConnectionString
    $start.EnvironmentVariables['MesIngest__SharedSecret'] = $Secret
    $start.EnvironmentVariables['MesIngest__ContinuousPollEnabled'] = $(if ($ContinuousPoll) { 'true' } else { 'false' })
    $start.EnvironmentVariables['MesIngest__RunOneShotOnStartup'] = 'false'
    if ($CleanupCheckIntervalSeconds -gt 0) {
        $start.EnvironmentVariables['MesIngest__HistoryCleanupCheckIntervalSeconds'] = [string]$CleanupCheckIntervalSeconds
    }
    if ($PollStartIntervalSeconds -gt 0) {
        $start.EnvironmentVariables['MesIngest__PollStartIntervalSeconds'] = [string]$PollStartIntervalSeconds
    }
    if (-not [string]::IsNullOrWhiteSpace($ReplayRecordingPath)) {
        $start.EnvironmentVariables['MesIngest__ReplayRoundsFromRecordingPath'] =
            [IO.Path]::GetFullPath($ReplayRecordingPath)
        $start.EnvironmentVariables['MesIngest__ReplayRoundsAcknowledgement'] =
            'RELEASE_SMOKE_NOT_FACTORY_EVIDENCE'
    }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Packaged Host did not start.' }

    $client = [Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(2)
        $client.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Secret)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($process.HasExited) { throw "Packaged Host exited during startup with code $($process.ExitCode)." }
            try {
                $response = $client.GetAsync($baseUrl + '/api/v2/contract').GetAwaiter().GetResult()
                if ($response.IsSuccessStatusCode) {
                    return [pscustomobject]@{ Process = $process; BaseUrl = $baseUrl }
                }
            } catch { }
            Start-Sleep -Milliseconds 250
        }
        throw 'Packaged Host did not expose /api/v2/contract within 45 seconds.'
    } catch {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
        $process.Dispose()
        throw
    } finally { $client.Dispose() }
}

function Stop-EvidenceHost {
    param([AllowNull()][object] $HostRun)
    if ($null -eq $HostRun) { return }
    $process = $HostRun.Process
    try {
        if (-not $process.HasExited) {
            $process.Kill()
            [void]$process.WaitForExit(10000)
        }
    } finally { $process.Dispose() }
}

function Invoke-HostGet {
    param(
        [Parameter(Mandatory = $true)][object] $HostRun,
        [Parameter(Mandatory = $true)][string] $Secret,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Path
    )
    $client = [Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
        $client.DefaultRequestHeaders.Authorization =
            [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $Secret)
        $started = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $response = $client.GetAsync($HostRun.BaseUrl + $Path).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $timer.Stop()
        $completed = [DateTimeOffset]::UtcNow
        if (-not $response.IsSuccessStatusCode) {
            throw "$Name returned HTTP $([int]$response.StatusCode)."
        }
        return [pscustomobject][ordered]@{
            name = $Name; path = $Path; startedAt = $started.ToString('o'); completedAt = $completed.ToString('o')
            statusCode = [int]$response.StatusCode; latencyMs = $timer.Elapsed.TotalMilliseconds
            responseBytes = [Text.Encoding]::UTF8.GetByteCount($body); body = $body
        }
    } finally { $client.Dispose() }
}

$boundedCurrentSurfaceFailurePrefixes = @{
    DemandSeries = 'DEMAND_SERIES'
    ExternallyReadableDemandCatalog = 'EXTERNALLY_READABLE_DEMAND_CATALOG'
    CurrentIngestAttention = 'CURRENT_INGEST_ATTENTION'
    Overview = 'OVERVIEW'
    ReadabilityAudit = 'READABILITY_AUDIT'
}
$historicalObjectSurfaceFailurePrefixes = @{
    PollTrace = 'POLL_TRACE'
    RawEvidence = 'RAW_EVIDENCE'
}
$historicalObjectMaxResponseBytes = @{
    PollTrace = 2097152L
    RawEvidence = 131072L
}

function Get-BoundedQuerySurfaceFailurePrefix {
    param([Parameter(Mandatory = $true)][string] $Surface)
    if ($boundedCurrentSurfaceFailurePrefixes.ContainsKey($Surface)) {
        return [string]$boundedCurrentSurfaceFailurePrefixes[$Surface]
    }
    if ($historicalObjectSurfaceFailurePrefixes.ContainsKey($Surface)) {
        return [string]$historicalObjectSurfaceFailurePrefixes[$Surface]
    }
    return $Surface.ToUpperInvariant()
}

function Get-EvidenceGateFailures {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $QueryEvidence,
        [int] $ActualPlanCount,
        [int] $StatementMetricCount,
        [Parameter(Mandatory = $true)][object] $RowCounts,
        [bool] $Tier1Satisfied,
        [AllowEmptyString()][string] $SourceCommit,
        [bool] $CanonicalScaleProfile,
        [string] $QuerySurface = 'All'
    )
    $failures = New-Object System.Collections.ArrayList
    if ($ActualPlanCount -eq 0) { [void]$failures.Add('MISSING_ACTUAL_PLAN') }
    if ($StatementMetricCount -eq 0) { [void]$failures.Add('MISSING_STATEMENT_METRICS') }
    $incompleteSurfaces = @($QueryEvidence | Where-Object {
        $runtimeIoProperty = $_.PSObject.Properties['runtimeIoComplete']
        $runtimeIoIncomplete = if ($null -eq $runtimeIoProperty) {
            $_.statementCount -eq 0
        } else {
            -not [bool]$runtimeIoProperty.Value
        }
        $_.actualPlanCount -eq 0 -or $runtimeIoIncomplete
    })
    if ($incompleteSurfaces.Count -gt 0) {
        [void]$failures.Add('MISSING_QUERY_SURFACE_EVIDENCE:' + (($incompleteSurfaces | Select-Object -ExpandProperty name) -join ','))
    }
    if ([long]$RowCounts.series_count -eq 0 -or [long]$RowCounts.raw_observations -eq 0) {
        [void]$failures.Add('EMPTY_SCALE_DATABASE')
    }
    if (-not $Tier1Satisfied) { [void]$failures.Add('SQL_TIER1_SKIPPED_OR_UNPROVEN') }
    if ([string]::IsNullOrWhiteSpace($SourceCommit) -or $SourceCommit -eq 'unknown') {
        [void]$failures.Add('MISSING_BUILD_IDENTITY')
    }
    if (-not $CanonicalScaleProfile) { [void]$failures.Add('NON_CANONICAL_SCALE_PROFILE') }
    $boundedScopes = if ($QuerySurface -eq 'All') {
        @($boundedCurrentSurfaceFailurePrefixes.Keys)
    } elseif ($boundedCurrentSurfaceFailurePrefixes.ContainsKey($QuerySurface)) {
        @($QuerySurface)
    } else { @() }
    foreach ($boundedScope in $boundedScopes) {
        $failurePrefix = Get-BoundedQuerySurfaceFailurePrefix $boundedScope
        $currentSurfaces = @(if ($boundedScope -eq 'DemandSeries') {
            $QueryEvidence | Where-Object { $_.name -like 'DemandSeries*' }
        } else {
            $QueryEvidence | Where-Object { $_.name -eq $boundedScope }
        })
        if ($currentSurfaces.Count -eq 0) {
            [void]$failures.Add("MISSING_$($failurePrefix)_EVIDENCE")
        } else {
            $requiredCurrentProperties = @(
                'rawObservationPlanOperators', 'rawObservationLogicalReads',
                'runtimeIoComplete', 'memoryGrantEvidenceComplete', 'spillCount',
                'maxGrantedMemoryKb')
            $currentShapeComplete = @($currentSurfaces | Where-Object {
                $surface = $_
                @($requiredCurrentProperties | Where-Object {
                    $null -eq $surface.PSObject.Properties[$_]
                }).Count -gt 0
            }).Count -eq 0
            if (-not $currentShapeComplete) {
                [void]$failures.Add("$($failurePrefix)_RUNTIME_IO_INCOMPLETE")
                [void]$failures.Add("$($failurePrefix)_MEMORY_GRANT_EVIDENCE_INCOMPLETE")
            } elseif (@($currentSurfaces | Where-Object {
                    [long]$_.rawObservationPlanOperators -ne 0 -or
                    [long]$_.rawObservationLogicalReads -ne 0 }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_RAW_HISTORY_READ")
            }
            if ($currentShapeComplete -and
                @($currentSurfaces | Where-Object { -not [bool]$_.runtimeIoComplete }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_RUNTIME_IO_INCOMPLETE")
            }
            if ($currentShapeComplete -and
                @($currentSurfaces | Where-Object { -not [bool]$_.memoryGrantEvidenceComplete }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_MEMORY_GRANT_EVIDENCE_INCOMPLETE")
            }
            if ($currentShapeComplete -and
                [long](($currentSurfaces | Measure-Object -Property spillCount -Sum).Sum) -ne 0) {
                [void]$failures.Add("$($failurePrefix)_SPILL")
            }
            if ($currentShapeComplete -and
                [long](($currentSurfaces | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum) -gt 8192) {
                [void]$failures.Add("$($failurePrefix)_ABNORMAL_MEMORY_GRANT")
            }
        }
    }
    if ($historicalObjectSurfaceFailurePrefixes.ContainsKey($QuerySurface)) {
        $failurePrefix = Get-BoundedQuerySurfaceFailurePrefix $QuerySurface
        $historicalSurfaces = @($QueryEvidence | Where-Object { $_.name -eq $QuerySurface })
        if ($historicalSurfaces.Count -eq 0) {
            [void]$failures.Add("MISSING_$($failurePrefix)_EVIDENCE")
        } else {
            if (@($historicalSurfaces | Where-Object { -not [bool]$_.runtimeIoComplete }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_RUNTIME_IO_INCOMPLETE")
            }
            if (@($historicalSurfaces | Where-Object { -not [bool]$_.memoryGrantEvidenceComplete }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_MEMORY_GRANT_EVIDENCE_INCOMPLETE")
            }
            if ([long](($historicalSurfaces | Measure-Object -Property spillCount -Sum).Sum) -ne 0) {
                [void]$failures.Add("$($failurePrefix)_SPILL")
            }
            if ([long](($historicalSurfaces | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum) -gt 8192) {
                [void]$failures.Add("$($failurePrefix)_ABNORMAL_MEMORY_GRANT")
            }
            if (@($historicalSurfaces | Where-Object { -not [bool]$_.objectKeySeekComplete }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_OBJECT_KEY_SEEK")
            }
            if ([long](($historicalSurfaces | Measure-Object -Property unrelatedHistoryScanCount -Sum).Sum) -ne 0) {
                [void]$failures.Add("$($failurePrefix)_UNRELATED_HISTORY_SCAN")
            }
            if ($QuerySurface -eq 'PollTrace' -and
                @($historicalSurfaces | Where-Object { -not [bool]$_.earliestIdentityComplete }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_EARLIEST_IDENTITY")
            }
            $maxResponseBytes = [long]$historicalObjectMaxResponseBytes[$QuerySurface]
            if (@($historicalSurfaces | Where-Object {
                    [long]$_.responseBytes -gt $maxResponseBytes -or
                    [long]$_.maxResponseBytes -ne $maxResponseBytes }).Count -gt 0) {
                [void]$failures.Add("$($failurePrefix)_RESPONSE_SIZE")
            }
        }
    }
    return @($failures)
}

if (-not [string]::IsNullOrWhiteSpace($ValidatePercentileFixture)) {
    $percentileValues = @($ValidatePercentileFixture.Split(',') | ForEach-Object {
        [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture)
    })
    Write-Output (
        'MESINGEST_PERCENTILE_FIXTURE: p50={0} p95={1} p99={2}' -f
        (Get-NearestRankPercentile $percentileValues 0.50).ToString([Globalization.CultureInfo]::InvariantCulture),
        (Get-NearestRankPercentile $percentileValues 0.95).ToString([Globalization.CultureInfo]::InvariantCulture),
        (Get-NearestRankPercentile $percentileValues 0.99).ToString([Globalization.CultureInfo]::InvariantCulture))
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($ValidateShowPlanFixturePath)) {
    if (-not (Test-Path -LiteralPath $ValidateShowPlanFixturePath -PathType Leaf)) {
        throw "ShowPlan fixture not found: $ValidateShowPlanFixturePath"
    }
    $runtimeIo = @(Read-ShowPlanRuntimeIo (Get-Content -Raw -LiteralPath $ValidateShowPlanFixturePath))
    Write-Output (
        'MESINGEST_SHOWPLAN_FIXTURE: operators={0} scans={1} logicalReads={2}' -f
        $runtimeIo.Count,
        (Get-TotalActualScans $runtimeIo),
        (Get-TotalActualLogicalReads $runtimeIo))
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($ValidateEvidenceFixturePath)) {
    if (-not (Test-Path -LiteralPath $ValidateEvidenceFixturePath -PathType Leaf)) {
        throw "Evidence fixture not found: $ValidateEvidenceFixturePath"
    }
    $fixture = Get-Content -Raw -LiteralPath $ValidateEvidenceFixturePath | ConvertFrom-Json
    $fixtureFailures = @(Get-EvidenceGateFailures `
        -QueryEvidence @($fixture.queries) `
        -ActualPlanCount @($fixture.actualPlans).Count `
        -StatementMetricCount @($fixture.statementMetrics).Count `
        -RowCounts $fixture.data `
        -Tier1Satisfied ([bool]$fixture.tests.satisfied) `
        -SourceCommit ([string]$fixture.build.sourceCommit) `
        -CanonicalScaleProfile ([bool]$fixture.profile.canonical) `
        -QuerySurface $QuerySurface)
    if ($fixtureFailures.Count -gt 0) {
        throw "Scale evidence fixture failed: $($fixtureFailures -join ', ')"
    }
    Write-Output 'MESINGEST_SCALE_EVIDENCE_FIXTURE: passed=True'
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($ValidateCapacityFixturePath)) {
    if (-not (Test-Path -LiteralPath $ValidateCapacityFixturePath -PathType Leaf)) {
        throw "Capacity fixture not found: $ValidateCapacityFixturePath"
    }
    $capacityFixture = Get-Content -Raw -LiteralPath $ValidateCapacityFixturePath | ConvertFrom-Json
    $capacityResult = Get-FastCapacityProjection $capacityFixture
    Write-Output "MESINGEST_FAST_CAPACITY_FIXTURE: passed=$($capacityResult.passed) escalationRequired=$($capacityResult.escalationRequired)"
    Write-Output ("projectedLogicalUsedMb={0:F3} projectedPhysicalDataMb={1:F3} projectedLdfMb={2:F3}" -f `
        [double]$capacityResult.prediction.logicalUsedMb, `
        [double]$capacityResult.prediction.physicalDataMb, `
        [double]$capacityResult.prediction.ldfMb)
    if (-not $capacityResult.passed) {
        throw "Fast capacity fixture failed: $(@($capacityResult.failures) -join ', ')"
    }
    exit 0
}

function Get-DeterministicContractEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $ExpectedSourceCommit,
        [Parameter(Mandatory = $true)][string] $ExpectedHostSha256
    )
    $requiredTests = @(
        'SingleFlightPollLoopTests.Uses_sixty_second_start_to_start_slots_and_skips_missed_slots_without_catch_up',
        'SingleFlightPollLoopTests.Consecutive_failures_back_off_60_120_300_then_success_resets_normal_cadence',
        'HistoryCleanupHostedServiceTests.Controlled_time_crosses_a_full_logical_day_on_twenty_four_exact_hourly_slots',
        'HistoryCleanupHostedServiceTests.Hourly_checks_keep_fixed_boundaries_without_catch_up_after_a_slow_batch',
        'HistoryRetentionStateTests.Retention_clocks_use_exact_thirty_day_boundaries',
        'WatchV2AutoRefreshTests.Settings_cover_all_five_host_data_views_and_expose_only_an_interval')
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            complete = $false; logicalDayBoundaryPassed = $false
            retrySchedulePassed = $false; buildBound = $false
            requiredTests = $requiredTests; passedTests = @()
        }
    }
    try {
        $attestation = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
        $trxPath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($Path))) ([string]$attestation.trxFile)
        if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw 'TRX missing.' }
        [xml]$trx = Get-Content -Raw -LiteralPath $trxPath
        $passedTests = @($trx.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Passed']") |
            ForEach-Object { [string]$_.testName })
        $missing = @($requiredTests | Where-Object {
            $suffix = $_
            @($passedTests | Where-Object { $_.EndsWith($suffix, [StringComparison]::Ordinal) }).Count -eq 0
        })
        $trxCounters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        $trxTotal = [int]$trxCounters.total
        $trxExecuted = [int]$trxCounters.executed
        $trxFailed = [int]$trxCounters.failed
        $trxPassed = [int]$trxCounters.passed
        $trxSkipped = $trxTotal - $trxExecuted
        $buildBound = [int]$attestation.schemaVersion -eq 1 -and
            [string]$attestation.sourceCommit -ceq $ExpectedSourceCommit -and
            [string]$attestation.hostSha256 -ceq $ExpectedHostSha256 -and
            [string]$attestation.trxSha256 -ceq (Get-FileSha256 $trxPath) -and
            [int]$attestation.failed -eq 0 -and [int]$attestation.skipped -eq 0 -and
            [int]$attestation.passed -eq $trxPassed -and [int]$attestation.total -eq $trxTotal -and
            $trxFailed -eq 0 -and $trxSkipped -eq 0 -and $trxPassed -ge $requiredTests.Count
        return [pscustomobject][ordered]@{
            complete = $missing.Count -eq 0 -and $buildBound
            logicalDayBoundaryPassed = $buildBound -and
                @($missing | Where-Object { $_ -like '*logical_day*' }).Count -eq 0
            retrySchedulePassed = $buildBound -and
                @($missing | Where-Object { $_ -like 'SingleFlightPollLoopTests.*' }).Count -eq 0
            buildBound = $buildBound
            sourceCommit = [string]$attestation.sourceCommit
            hostSha256 = [string]$attestation.hostSha256
            requiredTests = $requiredTests
            passedTests = @($passedTests | Where-Object {
                $name = $_
                @($requiredTests | Where-Object { $name.EndsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
            })
            missingTests = $missing
            trxFile = [IO.Path]::GetFileName($trxPath)
            trxSha256 = Get-FileSha256 $trxPath
        }
    } catch {
        return [pscustomobject][ordered]@{
            complete = $false; logicalDayBoundaryPassed = $false
            retrySchedulePassed = $false; buildBound = $false
            requiredTests = $requiredTests; passedTests = @()
            errorType = $_.Exception.GetType().Name
        }
    }
}

function Get-CapacityBlockerEvidence {
    param([AllowEmptyString()][string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or
        -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            preserved = $false; releaseBlocked = $true
            failures = @('TICKET27_CAPACITY_BLOCKER_EVIDENCE_MISSING')
            projectedLogicalUsedMb = $null; projectedPhysicalDataMb = $null
            projectedLdfMb = $null; linearityRatio = $null; nextValidation = $null
        }
    }
    try {
        $capacity = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
        $requiredFailures = @(
            'CAPACITY_GROWTH_NONLINEAR',
            'CAPACITY_LOGICAL_70_PERCENT_ESCALATION',
            'CAPACITY_PHYSICAL_70_PERCENT_ESCALATION')
        $observedFailures = @($capacity.failures | ForEach-Object { [string]$_ })
        $preserved = -not [bool]$capacity.passed -and
            [bool]$capacity.escalationRequired -and
            [bool]$capacity.escalation.releaseBlocked -and
            @($requiredFailures | Where-Object { $observedFailures -notcontains $_ }).Count -eq 0 -and
            [double]$capacity.model.projectedLogicalUsedMb -ge 21094.459 -and
            [double]$capacity.model.projectedPhysicalDataMb -ge 21128.0 -and
            [double]$capacity.model.linearityMaximumToMinimumSegmentRate -ge 2.42778
        return [pscustomobject][ordered]@{
            preserved = $preserved
            releaseBlocked = $true
            sourcePath = [IO.Path]::GetFullPath($Path)
            sha256 = Get-FileSha256 $Path
            failures = $observedFailures
            escalationRequired = [bool]$capacity.escalationRequired
            projectedLogicalUsedMb = [double]$capacity.model.projectedLogicalUsedMb
            projectedPhysicalDataMb = [double]$capacity.model.projectedPhysicalDataMb
            projectedLdfMb = [double]$capacity.model.projectedLdfMb
            linearityRatio = [double]$capacity.model.linearityMaximumToMinimumSegmentRate
            nextValidation = [string]$capacity.escalation.nextValidation
        }
    } catch {
        return [pscustomobject][ordered]@{
            preserved = $false; releaseBlocked = $true
            failures = @('TICKET27_CAPACITY_BLOCKER_EVIDENCE_INVALID')
            projectedLogicalUsedMb = $null; projectedPhysicalDataMb = $null
            projectedLdfMb = $null; linearityRatio = $null; nextValidation = $null
            errorType = $_.Exception.GetType().Name
        }
    }
}

function Get-FirstLastSlope {
    param(
        [Parameter(Mandatory = $true)][object[]] $Snapshots,
        [Parameter(Mandatory = $true)][string] $Property
    )
    if ($Snapshots.Count -lt 2) { return [double]::PositiveInfinity }
    $first = $Snapshots[0]
    $last = $Snapshots[$Snapshots.Count - 1]
    $minutes = ([DateTimeOffset]::Parse([string]$last.capturedAt) -
        [DateTimeOffset]::Parse([string]$first.capturedAt)).TotalMinutes
    if ($minutes -le 0) { return [double]::PositiveInfinity }
    return ([double]$last.$Property - [double]$first.$Property) / $minutes
}

function Get-StabilityResourceSnapshot {
    param(
        [Parameter(Mandatory = $true)][string] $MasterConnectionString,
        [Parameter(Mandatory = $true)][string] $DatabaseConnectionString,
        [Parameter(Mandatory = $true)][string] $DatabaseName,
        [Parameter(Mandatory = $true)][object] $HostRun,
        [Parameter(Mandatory = $true)][int] $SqlProcessId,
        [Parameter(Mandatory = $true)][string] $Stage
    )
    $capacity = Get-CapacityResourceSnapshot `
        $MasterConnectionString $DatabaseConnectionString $DatabaseName $Stage
    $sqlMemory = @(Invoke-SqlTable $MasterConnectionString @"
SELECT physical_memory_in_use_kb / 1024.0 AS processMemoryMb,
       locked_page_allocations_kb / 1024.0 AS lockedPagesMb,
       virtual_address_space_committed_kb / 1024.0 AS committedVirtualMb
FROM sys.dm_os_process_memory;
"@ @{}) | Select-Object -First 1
    $memoryCounters = @(Invoke-SqlTable $MasterConnectionString @"
SELECT counter_name, cntr_value
FROM sys.dm_os_performance_counters
WHERE object_name LIKE N'%Memory Manager%'
  AND counter_name IN
      (N'Total Server Memory (KB)', N'Granted Workspace Memory (KB)', N'Memory Grants Pending');
"@ @{})
    $waits = @(Invoke-SqlTable $MasterConnectionString @"
SELECT wait_type, waiting_tasks_count, wait_time_ms
FROM sys.dm_os_wait_stats
WHERE wait_type IN
      (N'RESOURCE_SEMAPHORE', N'RESOURCE_SEMAPHORE_QUERY_COMPILE',
       N'LCK_M_S', N'LCK_M_U', N'LCK_M_X', N'LCK_M_IX', N'LCK_M_IS');
"@ @{})
    $active = @(Invoke-SqlTable $MasterConnectionString @"
SELECT COUNT_BIG(*) AS pendingMemoryGrants,
       COALESCE(MAX(wait_time_ms), 0) AS maximumLockWaitMs,
       SUM(CASE WHEN blocking_session_id > 0 THEN 1 ELSE 0 END) AS blockedRequestCount
FROM sys.dm_exec_requests
WHERE database_id = DB_ID(@databaseName);
"@ @{ '@databaseName' = $DatabaseName }) | Select-Object -First 1
    $pendingGrants = @(Invoke-SqlTable $MasterConnectionString @"
SELECT COUNT_BIG(*) AS pendingMemoryGrants
FROM sys.dm_exec_query_memory_grants
WHERE grant_time IS NULL;
"@ @{}) | Select-Object -First 1
    $state = @(Invoke-SqlTable $DatabaseConnectionString @"
SELECT schemaInfo.EarliestAvailableHostUtc AS earliestAvailableHostUtc,
       CONVERT(nvarchar(36), schemaInfo.HistoryEpoch) AS historyEpoch,
       pressure.StoragePressureStatus AS storagePressureStatus,
       cleanup.HistoryCleanupStatus AS cleanupStatus,
       cleanup.HistoryCleanupTotalExpiredPollTraceCount AS expiredPollTraceCount,
       cleanup.HistoryCleanupTotalDeletedRawObservationCount AS deletedRawObservationCount,
       cleanup.HistoryCleanupTotalDeletedSeriesCount AS deletedSeriesCount,
       cleanup.HistoryCleanupLastFailureCode AS cleanupFailureCode,
       (SELECT COUNT_BIG(*) FROM mesingest.PollTraces) AS pollTraceCount,
       (SELECT COUNT_BIG(*) FROM mesingest.ProjectionCommits) AS projectionCommitCount,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations) AS rawObservationCount
FROM mesingest.SchemaInfo AS schemaInfo
CROSS JOIN mesingest.StoragePressureState AS pressure
CROSS JOIN mesingest.HistoryCleanupState AS cleanup
WHERE schemaInfo.Id = 1 AND pressure.Id = 1 AND cleanup.Id = 1;
"@ @{}) | Select-Object -First 1
    $storage = Get-StorageSnapshot $DatabaseConnectionString
    $hostProcess = Get-Process -Id $HostRun.Process.Id -ErrorAction Stop
    $sqlProcess = Get-Process -Id $SqlProcessId -ErrorAction Stop
    $totalServerMemory = @($memoryCounters | Where-Object { $_.counter_name -eq 'Total Server Memory (KB)' }) | Select-Object -First 1
    $workspaceMemory = @($memoryCounters | Where-Object { $_.counter_name -eq 'Granted Workspace Memory (KB)' }) | Select-Object -First 1
    $grantsPending = @($memoryCounters | Where-Object { $_.counter_name -eq 'Memory Grants Pending' }) | Select-Object -First 1
    $resourceWaits = @($waits | Where-Object { $_.wait_type -like 'RESOURCE_SEMAPHORE*' })
    $lockWaits = @($waits | Where-Object { $_.wait_type -like 'LCK_M_*' })
    return [pscustomobject][ordered]@{
        capturedAt = [DateTimeOffset]::UtcNow.ToString('o'); stage = $Stage
        logicalDatabaseUsedMb = [double]$storage.logicalUsedMb
        physicalDataFileMb = [double]$storage.physicalDataMb
        ldfMb = [double]$storage.ldfMb; logUsedMb = [double]$capacity.usedLogMb
        logReuseWait = [string]$capacity.logReuseWait
        databaseVersionStoreMb = [double]$capacity.databaseVersionStoreMb
        tempdbVersionStoreMb = [double]$capacity.tempdbVersionStoreMb
        tempdbUserObjectsMb = [double]$capacity.tempdbUserObjectsMb
        tempdbInternalObjectsMb = [double]$capacity.tempdbInternalObjectsMb
        tempdbUsedMb = [double]$capacity.tempdbVersionStoreMb +
            [double]$capacity.tempdbUserObjectsMb + [double]$capacity.tempdbInternalObjectsMb
        sqlProcessMemoryMb = [double]$sqlMemory.processMemoryMb
        sqlWorkingSetMb = $sqlProcess.WorkingSet64 / 1MB
        sqlCommittedVirtualMb = [double]$sqlMemory.committedVirtualMb
        sqlLockedPagesMb = [double]$sqlMemory.lockedPagesMb
        totalServerMemoryMb = if ($null -eq $totalServerMemory) { -1.0 } else { [double]$totalServerMemory.cntr_value / 1024.0 }
        grantedWorkspaceMemoryMb = if ($null -eq $workspaceMemory) { -1.0 } else { [double]$workspaceMemory.cntr_value / 1024.0 }
        memoryGrantsPending = if ($null -eq $grantsPending) { -1L } else { [long]$grantsPending.cntr_value }
        pendingMemoryGrants = [long]$pendingGrants.pendingMemoryGrants
        resourceSemaphoreWaitingTasks = [long](($resourceWaits | Measure-Object -Property waiting_tasks_count -Sum).Sum)
        resourceSemaphoreWaitMs = [long](($resourceWaits | Measure-Object -Property wait_time_ms -Sum).Sum)
        lockWaitingTasks = [long](($lockWaits | Measure-Object -Property waiting_tasks_count -Sum).Sum)
        lockWaitMs = [long](($lockWaits | Measure-Object -Property wait_time_ms -Sum).Sum)
        maximumLockWaitMs = [long]$active.maximumLockWaitMs
        blockedRequestCount = [long]$active.blockedRequestCount
        hostWorkingSetMb = $hostProcess.WorkingSet64 / 1MB
        hostPrivateMemoryMb = $hostProcess.PrivateMemorySize64 / 1MB
        hostHandleCount = [long]$hostProcess.HandleCount
        earliestAvailableHostUtc = ([DateTimeOffset]$state.earliestAvailableHostUtc).ToUniversalTime().ToString('o')
        historyEpoch = [string]$state.historyEpoch
        storagePressureStatus = [string]$state.storagePressureStatus
        cleanupStatus = [string]$state.cleanupStatus
        cleanupFailureCode = if ($null -eq $state.cleanupFailureCode) { $null } else { [string]$state.cleanupFailureCode
        }
        expiredPollTraceCount = [long]$state.expiredPollTraceCount
        deletedRawObservationCount = [long]$state.deletedRawObservationCount
        deletedSeriesCount = [long]$state.deletedSeriesCount
        pollTraceCount = [long]$state.pollTraceCount
        projectionCommitCount = [long]$state.projectionCommitCount
        rawObservationCount = [long]$state.rawObservationCount
    }
}

function Invoke-StabilityHttpBatch {
    param(
        [Parameter(Mandatory = $true)][Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][string] $DatabaseConnectionString,
        [AllowEmptyString()][string] $CatalogETag = '',
        [int] $BatchNumber = 0
    )
    $specs = @(
        [pscustomobject]@{ name = 'Overview'; path = '/api/v2/watch-overview'; catalog = $false },
        [pscustomobject]@{ name = 'CurrentIngestAttention'; path = '/api/v2/current-ingest-attention?pageSize=100'; catalog = $false },
        [pscustomobject]@{ name = 'DemandSeriesDefault'; path = '/api/v2/demand-series?pageSize=100'; catalog = $false },
        [pscustomobject]@{ name = 'DemandSeriesVisible'; path = '/api/v2/demand-series?pageSize=100&presence=VISIBLE'; catalog = $false },
        [pscustomobject]@{ name = 'DemandSeriesWorkType'; path = '/api/v2/demand-series?pageSize=100&workType=SMOKE_TYPE_A'; catalog = $false },
        [pscustomobject]@{ name = 'ReadabilityAudit'; path = '/api/v2/readability-audit?pageSize=100'; catalog = $false },
        [pscustomobject]@{ name = 'ErrorSearch'; path = '/api/v2/error-search?window=ALL_HISTORY&pageSize=100'; catalog = $false },
        [pscustomobject]@{ name = 'ExternallyReadableDemandCatalog'; path = '/api/v2/externally-readable-demand-catalog'; catalog = $true })
    $startedAt = [DateTimeOffset]::UtcNow
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $pending = New-Object System.Collections.ArrayList
    foreach ($spec in $specs) {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $BaseUrl + $spec.path)
        if ($spec.catalog -and -not [string]::IsNullOrWhiteSpace($CatalogETag)) {
            [void]$request.Headers.TryAddWithoutValidation('If-None-Match', $CatalogETag)
        }
        [void]$pending.Add([pscustomobject]@{
            spec = $spec; request = $request; task = $Client.SendAsync($request)
        })
    }
    [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($pending | ForEach-Object { $_.task })).GetAwaiter().GetResult()
    $samples = New-Object System.Collections.ArrayList
    $demandBody = $null
    foreach ($item in $pending) {
        try {
            $response = $item.task.GetAwaiter().GetResult()
            try {
                $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $statusCode = [int]$response.StatusCode
                if ($statusCode -ne 200 -and -not ($item.spec.catalog -and $statusCode -eq 304)) {
                    throw "$($item.spec.name) returned HTTP $statusCode."
                }
                if ($item.spec.name -eq 'DemandSeriesDefault') { $demandBody = $body }
                if ($item.spec.catalog -and $null -ne $response.Headers.ETag) {
                    $CatalogETag = [string]$response.Headers.ETag.ToString()
                }
                [void]$samples.Add([pscustomobject][ordered]@{
                    name = [string]$item.spec.name; batch = $BatchNumber
                    startedAt = $startedAt.ToString('o'); completedAt = [DateTimeOffset]::UtcNow.ToString('o')
                    statusCode = $statusCode; latencyMs = $timer.Elapsed.TotalMilliseconds
                    responseBytes = [Text.Encoding]::UTF8.GetByteCount($body)
                })
            } finally { $response.Dispose() }
        } finally { $item.request.Dispose() }
    }

    $frozenReadCount = 0L
    $frozenMismatchCount = 0L
    $projectionCommitsDuringFrozenReads = 0L
    $frozenWindowWithoutProjection = 0L
    if (($BatchNumber % 10) -eq 0 -and -not [string]::IsNullOrWhiteSpace($demandBody)) {
        $list = $demandBody | ConvertFrom-Json
        if (@($list.items).Count -gt 0 -and -not [string]::IsNullOrWhiteSpace([string]$list.snapshotReference)) {
            $seriesId = [string]$list.items[0].seriesId
            $snapshot = [Uri]::EscapeDataString([string]$list.snapshotReference)
            $commitBefore = [long](@(Invoke-SqlTable $DatabaseConnectionString `
                'SELECT COUNT_BIG(*) AS projectionCommitCount FROM mesingest.ProjectionCommits;' @{}) |
                Select-Object -First 1).projectionCommitCount
            $frozenWindowDeadline = [DateTimeOffset]::UtcNow.AddSeconds(2)
            do {
                $detailTimer = [Diagnostics.Stopwatch]::StartNew()
                $detailTasks = @(
                    1..8 | ForEach-Object {
                        $Client.GetAsync("$BaseUrl/api/v2/demand-series/$seriesId`?snapshot=$snapshot")
                    })
                [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]$detailTasks).GetAwaiter().GetResult()
                foreach ($detailTask in $detailTasks) {
                    $detailResponse = $detailTask.GetAwaiter().GetResult()
                    try {
                        $detailBody = $detailResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                        if (-not $detailResponse.IsSuccessStatusCode) {
                            throw "Frozen DemandSeries detail returned HTTP $([int]$detailResponse.StatusCode)."
                        }
                        $detail = $detailBody | ConvertFrom-Json
                        $frozenReadCount++
                        if ([string]$detail.snapshotReference -cne [string]$list.snapshotReference -or
                            [string]$detail.snapshot.historyEpoch -cne [string]$list.snapshot.historyEpoch -or
                            [string]$detail.snapshot.projectionCommitId -cne [string]$list.snapshot.projectionCommitId) {
                            $frozenMismatchCount++
                        }
                        [void]$samples.Add([pscustomobject][ordered]@{
                            name = 'DemandSeriesFrozenDetail'; batch = $BatchNumber
                            startedAt = [DateTimeOffset]::UtcNow.Subtract($detailTimer.Elapsed).ToString('o')
                            completedAt = [DateTimeOffset]::UtcNow.ToString('o')
                            statusCode = [int]$detailResponse.StatusCode; latencyMs = $detailTimer.Elapsed.TotalMilliseconds
                            responseBytes = [Text.Encoding]::UTF8.GetByteCount($detailBody)
                        })
                    } finally { $detailResponse.Dispose() }
                }
            } while ([DateTimeOffset]::UtcNow -lt $frozenWindowDeadline)
            $commitAfter = [long](@(Invoke-SqlTable $DatabaseConnectionString `
                'SELECT COUNT_BIG(*) AS projectionCommitCount FROM mesingest.ProjectionCommits;' @{}) |
                Select-Object -First 1).projectionCommitCount
            $projectionCommitsDuringFrozenReads = $commitAfter - $commitBefore
            if ($projectionCommitsDuringFrozenReads -le 0) {
                $frozenWindowWithoutProjection = 1
            }
        }
    }
    return [pscustomobject][ordered]@{
        samples = @($samples); catalogETag = $CatalogETag
        frozenReadCount = $frozenReadCount; frozenMismatchCount = $frozenMismatchCount
        projectionCommitsDuringFrozenReads = $projectionCommitsDuringFrozenReads
        frozenWindowWithoutProjection = $frozenWindowWithoutProjection
    }
}

function Invoke-PackagedWatchProbe {
    param(
        [Parameter(Mandatory = $true)][string] $PackageRoot,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][string] $Secret,
        [Parameter(Mandatory = $true)][string] $OutputPath
    )
    $watchExe = Join-Path $PackageRoot 'watch\MesIngest.Watch.exe'
    if (-not (Test-Path -LiteralPath $watchExe -PathType Leaf)) {
        throw "Packaged Watch executable not found: $watchExe"
    }
    $secretEnvironmentName = 'MESINGEST_TICKET28_WATCH_PROBE_SECRET'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $watchExe
    $start.WorkingDirectory = Split-Path -Parent $watchExe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.Arguments = '--ticket28-stability-probe "' + $BaseUrl + '" "' +
        $secretEnvironmentName + '" "' + ([IO.Path]::GetFullPath($OutputPath)) + '"'
    $start.EnvironmentVariables[$secretEnvironmentName] = $Secret
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Packaged Watch probe did not start.' }
    try {
        if (-not $process.WaitForExit(60000)) {
            $process.Kill(); [void]$process.WaitForExit(10000)
            throw 'Packaged Watch probe exceeded 60 seconds.'
        }
        if ($process.ExitCode -ne 0 -or
            -not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
            throw "Packaged Watch probe failed with exit code $($process.ExitCode)."
        }
        $receipt = Get-Content -Raw -LiteralPath $OutputPath | ConvertFrom-Json
        if (-not [bool]$receipt.passed -or [int]$receipt.readCount -ne 5) {
            throw 'Packaged Watch probe receipt is incomplete.'
        }
        return $receipt
    } finally { $process.Dispose() }
}

function Invoke-PackagedReferenceConsumerProbe {
    param(
        [Parameter(Mandatory = $true)][string] $PackageRoot,
        [Parameter(Mandatory = $true)][string] $BaseUrl,
        [Parameter(Mandatory = $true)][string] $Secret,
        [Parameter(Mandatory = $true)][string] $HistoryEpoch,
        [Parameter(Mandatory = $true)][string] $ForbiddenTokenPath
    )
    $consumerExe = Join-Path $PackageRoot 'reference-consumer\MesIngest.ReferenceConsumer.exe'
    if (-not (Test-Path -LiteralPath $consumerExe -PathType Leaf)) {
        throw "Packaged Reference Consumer executable not found: $consumerExe"
    }
    if (-not (Test-Path -LiteralPath $ForbiddenTokenPath -PathType Leaf)) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ForbiddenTokenPath), '')
    }
    $secretEnvironmentName = 'MESINGEST_TICKET28_REFERENCE_CONSUMER_SECRET'
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $consumerExe
    $start.WorkingDirectory = Split-Path -Parent $consumerExe
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = 'verify-cutover --base-url "' + $BaseUrl +
        '" --expected-history-epoch "' + $HistoryEpoch +
        '" --forbidden-key-token-file "' + ([IO.Path]::GetFullPath($ForbiddenTokenPath)) +
        '" --shared-secret-env "' + $secretEnvironmentName + '"'
    $start.EnvironmentVariables[$secretEnvironmentName] = $Secret
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Packaged Reference Consumer probe did not start.' }
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill(); [void]$process.WaitForExit(10000)
            throw 'Packaged Reference Consumer probe exceeded 60 seconds.'
        }
        if ($process.ExitCode -ne 0) {
            throw "Packaged Reference Consumer probe failed with exit code $($process.ExitCode) ($([string]$stderr).Length diagnostic characters)."
        }
        $receipt = $stdout | ConvertFrom-Json
        if ([string]$receipt.status -ne 'PASSED' -or
            [string]$receipt.historyEpoch -ne $HistoryEpoch) {
            throw 'Packaged Reference Consumer receipt is incomplete.'
        }
        return $receipt
    } finally { $process.Dispose() }
}

if (-not [string]::IsNullOrWhiteSpace($ValidateStabilityFixturePath)) {
    if (-not (Test-Path -LiteralPath $ValidateStabilityFixturePath -PathType Leaf)) {
        throw "Stability fixture not found: $ValidateStabilityFixturePath"
    }
    $stabilityFixture = Get-Content -Raw -LiteralPath $ValidateStabilityFixturePath | ConvertFrom-Json
    $stabilityResult = Get-AcceleratedStabilityResult $stabilityFixture
    Write-Output "MESINGEST_ACCELERATED_STABILITY_FIXTURE: passed=$($stabilityResult.passed) soakEscalationRequired=$($stabilityResult.soakEscalationRequired)"
    Write-Output ("p95LatencyMs={0} p99LatencyMs={1}" -f `
        [double]$stabilityFixture.latency.p95LatencyMs, `
        [double]$stabilityFixture.latency.p99LatencyMs)
    if (-not $stabilityResult.passed) {
        throw "Accelerated stability fixture failed: $(@($stabilityResult.failures) -join ', ')"
    }
    exit 0
}

$masterConnectionString = [Environment]::GetEnvironmentVariable($SqlConnectionStringEnvironmentVariable)
if ([string]::IsNullOrWhiteSpace($masterConnectionString)) {
    throw "Set $SqlConnectionStringEnvironmentVariable to an approved real SQL Server master connection."
}
$masterBuilder = [Data.SqlClient.SqlConnectionStringBuilder]::new($masterConnectionString)
if ($masterBuilder.DataSource.IndexOf('(localdb)', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Scale evidence requires a real SQL Server; LocalDB is rejected.'
}
if (-not [string]::Equals($masterBuilder.InitialCatalog, 'master', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Scale evidence connection must explicitly target master.'
}
$masterBuilder['Connect Timeout'] = 5
$masterBuilder['Application Name'] = 'MesIngest.ScaleEvidence.Control'
$masterConnectionString = $masterBuilder.ConnectionString

$serverIdentity = @(Invoke-SqlTable $masterConnectionString @"
SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS product_version,
       CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS product_major,
       CONVERT(int, SERVERPROPERTY('EngineEdition')) AS engine_edition,
       CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE ANY DATABASE')) AS can_create_database,
       CONVERT(int, HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION')) AS can_create_xevent,
       CONVERT(int, (SELECT value_in_use FROM sys.configurations WHERE name = N'max server memory (MB)')) AS max_server_memory_mb,
       CONVERT(int, SERVERPROPERTY('ProcessID')) AS process_id,
       CONVERT(nvarchar(4000), SERVERPROPERTY('ErrorLogFileName')) AS error_log_path;
"@) | Select-Object -First 1
if ($null -eq $serverIdentity -or $serverIdentity.can_create_database -ne 1 -or $serverIdentity.can_create_xevent -ne 1) {
    throw 'Scale evidence requires CREATE ANY DATABASE and ALTER ANY EVENT SESSION on a real SQL Server.'
}
$existing = @(Invoke-SqlTable $masterConnectionString 'SELECT name FROM sys.databases WHERE name = @databaseName;' @{ '@databaseName' = $DatabaseName })
if ($existing.Count -ne 0) {
    throw "Refusing to reuse existing or unverifiable database '$DatabaseName'."
}

$startedAt = [DateTimeOffset]::UtcNow
$runId = 'scale-{0}-{1}' -f $startedAt.ToString('yyyyMMddTHHmmssZ'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$runDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $runId
if (Test-Path -LiteralPath $runDirectory) { throw "Refusing to overwrite scale evidence run: $runDirectory" }
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$ownerProperty = 'MesIngestScaleEvidenceRunId'
$databaseCreated = $false
$databaseOwned = $false
$hostRun = $null
$xeventSession = ('MesIngestScale_' + $runId.Replace('-', '_'))
$xeventStarted = $false
$xelBase = ''
$secretBytes = [byte[]]::new(32)
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $random.GetBytes($secretBytes) } finally { $random.Dispose() }
$secret = [Convert]::ToBase64String($secretBytes)

$databaseBuilder = [Data.SqlClient.SqlConnectionStringBuilder]::new($masterConnectionString)
$databaseBuilder['Initial Catalog'] = $DatabaseName
$databaseConnectionString = $databaseBuilder.ConnectionString
$capacityCheckpoints = New-Object System.Collections.ArrayList
$capacityResourceSnapshots = New-Object System.Collections.ArrayList
$cleanupEvidence = $null
$capacityProjection = $null
$stabilityEvidence = $null
$stabilityResult = $null
$stabilityResourceSnapshots = New-Object System.Collections.ArrayList
$stabilityLatencySamples = New-Object System.Collections.ArrayList
$stabilityXEventSession = ''
$stabilityXEventStarted = $false
$stabilityXelBase = ''
$capacityBlocker = $null

# Bind long-running evidence to the exact package before the database or workload begins.
$sourceCommit = @(& git -C $ServiceRoot rev-parse HEAD 2>$null) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($sourceCommit)) {
    $versionFile = Join-Path (Split-Path -Parent $ServiceRoot) 'VERSION.txt'
    $sourceCommit = if (Test-Path $versionFile) {
        ([string](@(Get-Content $versionFile | Where-Object { $_ -like 'SourceCommit=*' } | Select-Object -First 1))).Substring('SourceCommit='.Length)
    } else { 'unknown' }
}
$sourceStatus = @(& git -C $ServiceRoot status --porcelain=v1 --untracked-files=normal 2>$null)
$sourceDirty = if ($LASTEXITCODE -eq 0) {
    $sourceStatus.Count -gt 0
} else {
    $versionFile = Join-Path (Split-Path -Parent $ServiceRoot) 'VERSION.txt'
    $dirtyLine = if (Test-Path $versionFile) { [string](@(Get-Content $versionFile | Where-Object { $_ -like 'SourceDirty=*' } | Select-Object -First 1)) } else { '' }
    if ($dirtyLine -eq 'SourceDirty=True') { $true } elseif ($dirtyLine -eq 'SourceDirty=False') { $false } else { $null }
}
$hostArtifactPath = if (Test-Path -LiteralPath (Join-Path $ServiceRoot 'MesIngest.Host.dll') -PathType Leaf) {
    Join-Path $ServiceRoot 'MesIngest.Host.dll'
} else {
    Join-Path $ServiceRoot 'MesIngest.Host.exe'
}
$hostArtifact = Get-Item -LiteralPath $hostArtifactPath
$hostSha256 = Get-FileSha256 $hostArtifact.FullName
$hostFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostArtifact.FullName).FileVersion

try {
    [void](Invoke-SqlNonQuery $masterConnectionString "CREATE DATABASE [$DatabaseName];")
    $databaseCreated = $true
    [void](Invoke-SqlNonQuery $masterConnectionString "ALTER DATABASE [$DatabaseName] SET RECOVERY SIMPLE;")

    # The durable owner marker precedes schema bootstrap so any later failure can still clean up
    # only this exact run's new database without trusting an in-memory created flag.
    [void](Invoke-SqlNonQuery $databaseConnectionString @"
EXEC sys.sp_addextendedproperty @name = N'$ownerProperty', @value = @runId;
"@ @{ '@runId' = $runId })
    $databaseOwned = $true

    # Bootstrap only through the production Host so schema/contract identity cannot drift.
    $hostRun = Start-EvidenceHost $databaseConnectionString $secret "MesIngest.ScaleEvidence.$runId.bootstrap"
    Stop-EvidenceHost $hostRun
    $hostRun = $null

    $schemaIdentity = @(Invoke-SqlTable $databaseConnectionString @"
SELECT SchemaVersion AS schemaVersion, ContractVersion AS contractVersion,
       CONVERT(nvarchar(36), HistoryEpoch) AS historyEpoch
FROM mesingest.SchemaInfo WHERE Id = 1;
"@) | Select-Object -First 1
    if ($null -eq $schemaIdentity) { throw 'Production Host did not bootstrap SchemaInfo.' }
    $databaseConfiguration = @(Invoke-SqlTable $masterConnectionString @"
SELECT compatibility_level AS compatibilityLevel, recovery_model_desc AS recoveryModel
FROM sys.databases WHERE name = @databaseName;
"@ @{ '@databaseName' = $DatabaseName }) | Select-Object -First 1
    if ($FastCapacityProjection) {
        [void]$capacityResourceSnapshots.Add((Get-CapacityResourceSnapshot `
            $masterConnectionString $databaseConnectionString $DatabaseName 'after-bootstrap'))
    }

    $hostSessionId = [string](@(Invoke-SqlTable $databaseConnectionString @"
SELECT TOP (1) HostSessionId FROM mesingest.HostSessions ORDER BY StartedAt DESC, HostSessionId DESC;
"@) | Select-Object -First 1).HostSessionId
    $baselineSequence = $historyRoundCount + 1

    # Create deterministic poll/commit history in bounded batches. Explicit identity values put the
    # baseline current projection after every historical round even though domain rows reference it.
    for ($roundStart = 1L; $roundStart -le $historyRoundCount; $roundStart += $RoundBatchSize) {
        $roundCount = [Math]::Min([long]$RoundBatchSize, $historyRoundCount - $roundStart + 1)
        [void](Invoke-SqlNonQuery $databaseConnectionString @"
SET IDENTITY_INSERT mesingest.PollTraces ON;
;WITH n AS
(
    SELECT TOP (CONVERT(int, @roundCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.PollTraces
    (PollTraceId, PollTraceSequence, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
SELECT N'scale-poll-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + n), 10),
       @roundStart + n, N'MES_TASK_UNION/scale-v1', N'SUCCESS',
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + n) + 1) * @roundSeconds), @anchorUtc),
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + n) + 1) * @roundSeconds) + 1, @anchorUtc),
       @observationsPerRound, REPLICATE('0', 64)
FROM n;
SET IDENTITY_INSERT mesingest.PollTraces OFF;

SET IDENTITY_INSERT mesingest.ProjectionCommits ON;
;WITH n AS
(
    SELECT TOP (CONVERT(int, @roundCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.ProjectionCommits
    (ProjectionCommitId, ProjectionSequence, PollTraceId, CommittedAt, HostSessionId,
     RestartPhaseBefore, RestartPhaseAfter, AbsenceAuthority, CatalogRevision, HistoryEpoch,
     OverviewActiveErrorSeriesCount, OverviewPrior7DaysErrorSeriesCount)
SELECT N'scale-commit-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + n), 10),
       @roundStart + n,
       N'scale-poll-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + n), 10),
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + n) + 1) * @roundSeconds), @anchorUtc),
       @hostSessionId, N'NORMAL', N'NORMAL', 1, 0,
       CONVERT(uniqueidentifier, @historyEpoch), 0, 0
FROM n;
SET IDENTITY_INSERT mesingest.ProjectionCommits OFF;
"@ @{
                '@roundStart' = $roundStart; '@roundCount' = $roundCount
                '@historyRoundCount' = $historyRoundCount; '@roundSeconds' = $RoundIntervalSeconds
                '@anchorUtc' = $AnchorUtc; '@observationsPerRound' = $ObservationsPerRound
                '@hostSessionId' = $hostSessionId; '@historyEpoch' = [string]$schemaIdentity.historyEpoch
            })
    }

    [void](Invoke-SqlNonQuery $databaseConnectionString @"
SET IDENTITY_INSERT mesingest.PollTraces ON;
INSERT mesingest.PollTraces
    (PollTraceId, PollTraceSequence, QueryVersion, Outcome, StartedAt, CompletedAt, [RowCount], ContentDigest)
VALUES (N'scale-baseline-poll', @baselineSequence, N'MES_TASK_UNION/scale-v1', N'SUCCESS',
        DATEADD(second, -1, @anchorUtc), @anchorUtc, @seriesCount, REPLICATE('1', 64));
SET IDENTITY_INSERT mesingest.PollTraces OFF;
SET IDENTITY_INSERT mesingest.ProjectionCommits ON;
INSERT mesingest.ProjectionCommits
    (ProjectionCommitId, ProjectionSequence, PollTraceId, CommittedAt, HostSessionId,
     RestartPhaseBefore, RestartPhaseAfter, AbsenceAuthority, CatalogRevision, HistoryEpoch,
     OverviewActiveErrorSeriesCount, OverviewPrior7DaysErrorSeriesCount)
VALUES (N'scale-baseline-commit', @baselineSequence, N'scale-baseline-poll', @anchorUtc,
        @hostSessionId, N'NORMAL', N'NORMAL', 1, 1, CONVERT(uniqueidentifier, @historyEpoch),
        @errorCount, @errorCount);
SET IDENTITY_INSERT mesingest.ProjectionCommits OFF;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeries
    (SeriesId, KeyToken, WorkType, Sublot, Lifecycle, CurrentPresence, StartedAt, ArchivedAt,
     CreatedPollTraceId, CreatedProjectionCommitId, LatestProjectionCommitId, CurrentDemandId, LastSeriesSequence)
SELECT N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       LOWER(CONVERT(char(64), HASHBYTES('SHA2_256', CONCAT(N'SUBLOT-', n, N'|WT-', n % 4)), 2)),
       N'WT-' + CONVERT(nvarchar(10), n % 4), N'SUBLOT-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       CASE WHEN n < @activeCount THEN N'TRACKING' ELSE N'ARCHIVED' END,
       CASE WHEN n < @activeCount THEN N'VISIBLE' ELSE N'GONE' END,
       DATEADD(minute, -n, @anchorUtc), CASE WHEN n < @activeCount THEN NULL ELSE @anchorUtc END,
       N'scale-baseline-poll', N'scale-baseline-commit', N'scale-baseline-commit', NULL,
       CASE WHEN n < @errorCount THEN 2 ELSE 1 END
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.TransportDemands
    (DemandId, SeriesId, Generation, PredecessorDemandId, Status, CreatedAt, DemandLastSeenAt,
     GoneConfirmedAt, CreatedPollTraceId, CreatedProjectionCommitId, LatestProjectionCommitId,
     LatestObservationProjectionCommitId, CurrentRawObservationCount, DemandRevision,
     ValueObservedAt, Area, Eqp, Step, MesSourceDate, Package)
SELECT N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6), 1, NULL,
       CASE WHEN n < @activeCount THEN N'VISIBLE' ELSE N'GONE' END,
       DATEADD(minute, -n, @anchorUtc), @anchorUtc,
       CASE WHEN n < @activeCount THEN NULL ELSE @anchorUtc END,
       N'scale-baseline-poll', N'scale-baseline-commit', N'scale-baseline-commit',
       N'scale-baseline-commit', 1, 1, @anchorUtc,
       CASE WHEN n < @errorCount THEN N'INVALID AREA' ELSE N'A' + CONVERT(nvarchar(10), (n % 9) + 1) + N'-' + CONVERT(nvarchar(10), (n % 9) + 1) END,
       N'EQP-' + RIGHT(REPLICATE('0', 3) + CONVERT(varchar(10), n % 40), 3),
       N'STEP-' + CONVERT(nvarchar(10), n % 12), @anchorUtc,
       N'PKG-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6)
FROM n;

UPDATE s SET CurrentDemandId = N'scale-demand-' + RIGHT(REPLICATE('0', 6) + SUBSTRING(s.SeriesId, LEN(s.SeriesId) - 5, 6), 6)
FROM mesingest.DemandSeries s;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandRawObservations
    (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId, WorkType, Sublot,
     Area, Eqp, Step, MesSourceDate, Package, MesSourceDateRaw)
SELECT N'scale-baseline-poll', CONVERT(int, n), N'scale-baseline-commit',
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'WT-' + CONVERT(nvarchar(10), n % 4), N'SUBLOT-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       CASE WHEN n < @errorCount THEN N'INVALID AREA' ELSE N'A' + CONVERT(nvarchar(10), (n % 9) + 1) + N'-' + CONVERT(nvarchar(10), (n % 9) + 1) END,
       N'EQP-' + RIGHT(REPLICATE('0', 3) + CONVERT(varchar(10), n % 40), 3),
       N'STEP-' + CONVERT(nvarchar(10), n % 12), @anchorUtc,
       N'PKG-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6), CONVERT(nvarchar(33), @anchorUtc, 127)
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @seriesCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesEvents
    (EventId, SeriesId, SeriesSequence, EventType, OccurredAt, SubjectKind, SubjectId,
     PollTraceId, ProjectionCommitId, PayloadVersion, Payload)
SELECT N'scale-start-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       1, N'SERIES_STARTED', DATEADD(minute, -n, @anchorUtc), N'SERIES',
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-baseline-poll', N'scale-baseline-commit', 1, N'{}'
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesEvents
    (EventId, SeriesId, SeriesSequence, EventType, OccurredAt, SubjectKind, SubjectId,
     PollTraceId, ProjectionCommitId, PayloadVersion, Payload)
SELECT N'scale-error-open-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       2, N'ERROR_PERIOD_STARTED', @anchorUtc, N'DEMAND',
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-baseline-poll', N'scale-baseline-commit', 1, N'{}'
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesErrorPeriods
    (PeriodId, SeriesId, ErrorCode, Category, Severity, Target, SubjectKind, StartReason,
     StartedAt, EndedAt, EndReason, OpenedEventId, ClosedEventId)
SELECT N'scale-period-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'INVALID_MES_FIELD_FORMAT', N'DATA_FORMAT', N'ERROR', N'AREA', N'DEMAND', N'OBSERVED',
       @anchorUtc, NULL, NULL,
       N'scale-error-open-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6), NULL
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.SeriesErrorPeriodEvidence
    (EvidenceId, PeriodId, EventId, EvidenceKind, ObservedAt, PollTraceId, ProjectionCommitId,
     DemandId, ObservedValue, ExpectedRule)
SELECT N'scale-evidence-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-period-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-error-open-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'FIELD_VALIDATION', @anchorUtc, N'scale-baseline-poll', N'scale-baseline-commit',
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'INVALID AREA', N'AREA must match the MES AREA domain format.'
FROM n;

;WITH n AS
(
    SELECT TOP (CONVERT(int, @errorCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandSeriesCurrentConditions
    (SeriesId, ErrorCode, Target, SubjectKind, PeriodId, LatestEvidenceId)
SELECT N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'INVALID_MES_FIELD_FORMAT', N'AREA', N'DEMAND',
       N'scale-period-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6),
       N'scale-evidence-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), n), 6)
FROM n;

INSERT mesingest.CatalogItems
    (DemandId, SeriesId, WorkType, Sublot, Generation, DemandRevision, CreatedAt, ValueObservedAt,
     ValuePollTraceId, ValueProjectionCommitId, Area, Eqp, Step, MesSourceDate, Package)
SELECT d.DemandId, d.SeriesId, s.WorkType, s.Sublot, d.Generation, d.DemandRevision,
       d.CreatedAt, d.ValueObservedAt, N'scale-baseline-poll', N'scale-baseline-commit',
       d.Area, d.Eqp, d.Step, d.MesSourceDate, d.Package
FROM mesingest.TransportDemands d
JOIN mesingest.DemandSeries s ON s.SeriesId = d.SeriesId
WHERE s.Lifecycle = N'TRACKING'
  AND NOT EXISTS (SELECT 1 FROM mesingest.DemandSeriesCurrentConditions c WHERE c.SeriesId = s.SeriesId);
UPDATE mesingest.CatalogState SET CatalogRevision = 1, ProjectionCommitId = N'scale-baseline-commit' WHERE Id = 1;
INSERT mesingest.ProjectionCommitUnassignedObservationFacts (ProjectionCommitId, ObservationCount, ContentDigest)
VALUES (N'scale-baseline-commit', 0, NULL);

INSERT mesingest.CurrentOverviewAreaFacts
    (AreaKey, IsGlobal, Area, ExactTotalDemandCount, ReadableCount, ProjectionCommitId)
SELECT N'G' + REPLICATE(N'0', 64), 1, NULL, COUNT_BIG(*),
    COALESCE(SUM(CONVERT(BIGINT, CASE WHEN catalogItem.DemandId IS NULL THEN 0 ELSE 1 END)), 0),
    N'scale-baseline-commit'
FROM mesingest.DemandSeries AS series
INNER JOIN mesingest.TransportDemands AS demand ON demand.DemandId = series.CurrentDemandId
LEFT JOIN mesingest.CatalogItems AS catalogItem ON catalogItem.DemandId = demand.DemandId
UNION ALL
SELECT N'A' + CONVERT(CHAR(64), HASHBYTES(
        'SHA2_256', CONVERT(VARBINARY(MAX),
            demand.Area COLLATE Latin1_General_100_BIN2)), 2),
    0, demand.Area COLLATE Latin1_General_100_BIN2, COUNT_BIG(*),
    COALESCE(SUM(CONVERT(BIGINT, CASE WHEN catalogItem.DemandId IS NULL THEN 0 ELSE 1 END)), 0),
    N'scale-baseline-commit'
FROM mesingest.DemandSeries AS series
INNER JOIN mesingest.TransportDemands AS demand ON demand.DemandId = series.CurrentDemandId
LEFT JOIN mesingest.CatalogItems AS catalogItem ON catalogItem.DemandId = demand.DemandId
WHERE demand.Area IS NOT NULL
GROUP BY demand.Area COLLATE Latin1_General_100_BIN2;

;WITH RecentSeries AS
(
    SELECT TOP (5) eventRow.EventId, eventRow.OccurredAt, eventRow.SeriesId,
        series.WorkType, eventRow.PollTraceId, eventRow.ProjectionCommitId,
        ROW_NUMBER() OVER (ORDER BY eventRow.OccurredAt DESC, eventRow.EventId) AS ActivityRank
    FROM mesingest.DemandSeriesEvents AS eventRow
    INNER JOIN mesingest.DemandSeries AS series ON series.SeriesId = eventRow.SeriesId
    ORDER BY eventRow.OccurredAt DESC, eventRow.EventId
)
INSERT mesingest.CurrentOverviewActivities
    (ActivityRank, SnapshotProjectionCommitId, EventId, Kind, EventType, Severity,
     OccurredAt, SeriesId, WorkType, PollTraceId, SourceProjectionCommitId, NavigationTarget)
SELECT CONVERT(TINYINT, ActivityRank), N'scale-baseline-commit', EventId,
    N'SERIES_LIFECYCLE', N'DEMAND_SERIES_STARTED', N'WARNING', OccurredAt,
    SeriesId, WorkType, PollTraceId, ProjectionCommitId, N'DEMAND_SERIES_DETAIL'
FROM RecentSeries;
"@ @{
            '@baselineSequence' = $baselineSequence; '@anchorUtc' = $AnchorUtc
            '@hostSessionId' = $hostSessionId; '@seriesCount' = $SeriesCount
            '@historyEpoch' = [string]$schemaIdentity.historyEpoch
            '@activeCount' = [int][Math]::Round($SeriesCount * 0.70, 0, [MidpointRounding]::AwayFromZero)
            '@errorCount' = [Math]::Max(1, [int][Math]::Round($SeriesCount * 0.10, 0, [MidpointRounding]::AwayFromZero))
        })

    # Inflate history after the coherent current graph exists; every batch is independently committed.
    if ($FastCapacityProjection -and $historyRoundCount -gt 0) {
        $checkpointStorage = Get-StorageSnapshot $databaseConnectionString
        [void]$capacityCheckpoints.Add([pscustomobject][ordered]@{
            historyRoundCount = 0L
            historyObservationCount = 0L
            logicalUsedMb = [double]$checkpointStorage.logicalUsedMb
            physicalDataMb = [double]$checkpointStorage.physicalDataMb
            ldfMb = [double]$checkpointStorage.ldfMb
        })
    }
    for ($roundStart = 1L; $roundStart -le $historyRoundCount; $roundStart += $RoundBatchSize) {
        $roundCount = [Math]::Min([long]$RoundBatchSize, $historyRoundCount - $roundStart + 1)
        [void](Invoke-SqlNonQuery $databaseConnectionString @"
;WITH rounds AS
(
    SELECT TOP (CONVERT(int, @roundCount)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
), observations AS
(
    SELECT TOP (CONVERT(int, @observationsPerRound)) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT mesingest.DemandRawObservations
    (PollTraceId, Ordinal, ProjectionCommitId, SeriesId, DemandId, WorkType, Sublot,
     Area, Eqp, Step, MesSourceDate, Package, MesSourceDateRaw)
SELECT N'scale-poll-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + r.n), 10),
       CONVERT(int, o.n),
       N'scale-commit-' + RIGHT(REPLICATE('0', 10) + CONVERT(varchar(20), @roundStart + r.n), 10),
       N'scale-series-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       N'scale-demand-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       N'WT-' + CONVERT(nvarchar(10), (o.n % @seriesCount) % 4),
       N'SUBLOT-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       CASE WHEN (o.n % @seriesCount) < @errorCount THEN N'INVALID AREA'
            ELSE N'A' + CONVERT(nvarchar(10), ((o.n % @seriesCount) % 9) + 1) + N'-' + CONVERT(nvarchar(10), ((o.n % @seriesCount) % 9) + 1) END,
       N'EQP-' + RIGHT(REPLICATE('0', 3) + CONVERT(varchar(10), (o.n % @seriesCount) % 40), 3),
       N'STEP-' + CONVERT(nvarchar(10), (o.n % @seriesCount) % 12),
       DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + r.n) + 1) * @roundSeconds), @anchorUtc),
       N'PKG-' + RIGHT(REPLICATE('0', 6) + CONVERT(varchar(10), o.n % @seriesCount), 6),
       CONVERT(nvarchar(33), DATEADD(second, -CONVERT(bigint, (@historyRoundCount - (@roundStart + r.n) + 1) * @roundSeconds), @anchorUtc), 127)
FROM rounds r CROSS JOIN observations o;
"@ @{
                '@roundStart' = $roundStart; '@roundCount' = $roundCount
                '@observationsPerRound' = $ObservationsPerRound; '@seriesCount' = $SeriesCount
                '@errorCount' = [Math]::Max(1, [int][Math]::Round($SeriesCount * 0.10))
                '@historyRoundCount' = $historyRoundCount; '@roundSeconds' = $RoundIntervalSeconds; '@anchorUtc' = $AnchorUtc
            })
        if ($FastCapacityProjection) {
            $completedRounds = $roundStart + $roundCount - 1
            $checkpointStorage = Get-StorageSnapshot $databaseConnectionString
            [void]$capacityCheckpoints.Add([pscustomobject][ordered]@{
                historyRoundCount = [long]$completedRounds
                historyObservationCount = [long]$completedRounds * [long]$ObservationsPerRound
                logicalUsedMb = [double]$checkpointStorage.logicalUsedMb
                physicalDataMb = [double]$checkpointStorage.physicalDataMb
                ldfMb = [double]$checkpointStorage.ldfMb
            })
        }
    }

    [void](Invoke-SqlNonQuery $databaseConnectionString @"
UPDATE mesingest.SchemaInfo
SET EarliestAvailableHostUtc =
    (SELECT MIN(CompletedAt) FROM mesingest.PollTraces)
WHERE Id = 1;
"@ @{})

    [void](Invoke-SqlNonQuery $databaseConnectionString 'EXEC sys.sp_updatestats;' @{} 0)
    if ($FastCapacityProjection) {
        [void]$capacityResourceSnapshots.Add((Get-CapacityResourceSnapshot `
            $masterConnectionString $databaseConnectionString $DatabaseName 'after-load'))
    }
    $rowCounts = @(Invoke-SqlTable $databaseConnectionString @"
SELECT (SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations) AS raw_observations,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries) AS series_count,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries WHERE Lifecycle = N'TRACKING') AS active_series,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries WHERE Lifecycle = N'ARCHIVED') AS archived_series,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeriesErrorPeriods) AS error_periods;
"@) | Select-Object -First 1
    if ($null -eq $rowCounts -or [long]$rowCounts.raw_observations -ne $expectedTotalObservationCount -or [long]$rowCounts.series_count -eq 0) {
        throw 'EMPTY_SCALE_DATABASE: generated row counts do not match the selected profile.'
    }

    $errorLogDirectory = Split-Path -Parent ([string]$serverIdentity.error_log_path)
    $xelBase = Join-Path $errorLogDirectory ($xeventSession + '.xel')
    $escapedXel = $xelBase.Replace("'", "''")
    $escapedSession = $xeventSession.Replace(']', ']]')
    $appName = "MesIngest.ScaleEvidence.$runId.workload"
    $escapedApp = $appName.Replace("'", "''")
    [void](Invoke-SqlNonQuery $masterConnectionString @"
CREATE EVENT SESSION [$escapedSession] ON SERVER
ADD EVENT sqlserver.sql_statement_completed
(
    ACTION(sqlserver.client_app_name, sqlserver.database_id, sqlserver.session_id, sqlserver.sql_text)
    WHERE (sqlserver.client_app_name = N'$escapedApp')
),
ADD EVENT sqlserver.query_post_execution_showplan
(
    ACTION(sqlserver.client_app_name, sqlserver.database_id, sqlserver.session_id, sqlserver.sql_text)
    WHERE (sqlserver.client_app_name = N'$escapedApp')
)
ADD TARGET package0.event_file(SET filename = N'$escapedXel', max_file_size = 1024, max_rollover_files = 4)
WITH (MAX_MEMORY = 16384 KB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
      MAX_DISPATCH_LATENCY = 1 SECONDS, TRACK_CAUSALITY = ON, STARTUP_STATE = OFF);
ALTER EVENT SESSION [$escapedSession] ON SERVER STATE = START;
"@)
    $xeventStarted = $true

    $hostRun = Start-EvidenceHost $databaseConnectionString $secret $appName
    $pollTracePath = if ($historyRoundCount -gt 0) {
        '/api/v2/poll-traces/scale-poll-' + (1L).ToString('0000000000')
    } else {
        '/api/v2/poll-traces/scale-baseline-poll'
    }
    $surfaceCatalog = @(
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesDefault'; path = '/api/v2/demand-series?pageSize=100' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesVisible'; path = '/api/v2/demand-series?pageSize=100&presence=VISIBLE' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesGone'; path = '/api/v2/demand-series?pageSize=100&presence=GONE' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesArchived'; path = '/api/v2/demand-series?pageSize=100&lifecycle=ARCHIVED' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesArea'; path = '/api/v2/demand-series?pageSize=100&area=A1-1' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesWorkType'; path = '/api/v2/demand-series?pageSize=100&workType=WT-1' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesSublot'; path = '/api/v2/demand-series?pageSize=100&sublot=SUBLOT-0001' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesSeriesId'; path = '/api/v2/demand-series?pageSize=100&seriesId=scale-series-000001' },
        [ordered]@{ scope = 'DemandSeries'; kind = 'http'; name = 'DemandSeriesDemandId'; path = '/api/v2/demand-series?pageSize=100&demandId=scale-demand-000001' },
        [ordered]@{ scope = 'ExternallyReadableDemandCatalog'; kind = 'http'; name = 'ExternallyReadableDemandCatalog'; path = '/api/v2/externally-readable-demand-catalog' },
        [ordered]@{ scope = 'CurrentIngestAttention'; kind = 'http'; name = 'CurrentIngestAttention'; path = '/api/v2/current-ingest-attention?pageSize=100' },
        [ordered]@{ scope = 'Overview'; kind = 'http'; name = 'Overview'; path = '/api/v2/watch-overview' },
        [ordered]@{ scope = 'ReadabilityAudit'; kind = 'http'; name = 'ReadabilityAudit'; path = '/api/v2/readability-audit?pageSize=100' },
        [ordered]@{ scope = 'ErrorSearch'; kind = 'http'; name = 'ErrorSearch'; path = '/api/v2/error-search?window=ALL_HISTORY&pageSize=100' },
        [ordered]@{ scope = 'PollTrace'; kind = 'http'; name = 'PollTrace'; path = $pollTracePath; maxResponseBytes = $historicalObjectMaxResponseBytes.PollTrace },
        [ordered]@{ scope = 'RawEvidence'; kind = 'raw-evidence'; name = 'RawEvidence'; path = '/raw-observations'; maxResponseBytes = $historicalObjectMaxResponseBytes.RawEvidence }
    )
    $selectedCatalog = if ($QuerySurface -eq 'All') {
        @($surfaceCatalog)
    } else {
        @($surfaceCatalog | Where-Object { $_.scope -eq $QuerySurface })
    }
    $surfaces = @($selectedCatalog | Where-Object { $_.kind -eq 'http' })
    $measurements = New-Object System.Collections.ArrayList
    foreach ($surface in $surfaces) {
        for ($i = 0; $i -lt ($WarmupCount + $MeasurementCount); $i++) {
            $sample = Invoke-HostGet $hostRun $secret $surface.name $surface.path
            if ($i -ge $WarmupCount) { [void]$measurements.Add($sample) }
        }
    }

    $measureRawEvidence = @($selectedCatalog | Where-Object { $_.kind -eq 'raw-evidence' }).Count -gt 0
    if ($measureRawEvidence) {
        $errorList = Invoke-HostGet $hostRun $secret 'ErrorSearchDiscovery' '/api/v2/error-search?window=ALL_HISTORY&pageSize=100'
        $errorJson = $errorList.body | ConvertFrom-Json
        if (@($errorJson.items).Count -eq 0) { throw 'ErrorSearch scale seed produced no error item.' }
        $seriesId = [string]$errorJson.items[0].seriesId
        $snapshotReference = [Uri]::EscapeDataString([string]$errorJson.snapshotReference)
        $detail = Invoke-HostGet $hostRun $secret 'ErrorSearchDetail' ("/api/v2/error-search/$seriesId`?snapshot=$snapshotReference")
        $detailJson = $detail.body | ConvertFrom-Json
        $evidenceId = [string]$detailJson.periods[0].evidence[0].evidenceId
        $rawPath = "/api/v2/error-search/$seriesId/evidence/$evidenceId/raw-observations?fields=area&maxItems=20&snapshot=$snapshotReference"
        for ($i = 0; $i -lt ($WarmupCount + $MeasurementCount); $i++) {
            $sample = Invoke-HostGet $hostRun $secret 'RawEvidence' $rawPath
            if ($i -ge $WarmupCount) { [void]$measurements.Add($sample) }
        }
    }
    Stop-EvidenceHost $hostRun
    $hostRun = $null
    Start-Sleep -Seconds 2
    [void](Invoke-SqlNonQuery $masterConnectionString "ALTER EVENT SESSION [$escapedSession] ON SERVER STATE = STOP;")
    $xeventStarted = $false

    $xelPattern = $xelBase.Substring(0, $xelBase.Length - 4) + '*.xel'
    $events = @(Invoke-SqlTable $masterConnectionString @"
SELECT object_name,
       file_name,
       CONVERT(nvarchar(max), event_data) AS event_xml
FROM sys.fn_xe_file_target_read_file(@xelPattern, NULL, NULL, NULL)
ORDER BY file_name, file_offset;
"@ @{ '@xelPattern' = $xelPattern } 300)
    $statementEvents = @($events | Where-Object { $_.object_name -eq 'sql_statement_completed' })
    $planEvents = @($events | Where-Object { $_.object_name -eq 'query_post_execution_showplan' })
    if ($planEvents.Count -eq 0) { throw 'MISSING_ACTUAL_PLAN: query_post_execution_showplan returned no workload plan.' }

    $statementMetrics = foreach ($event in $statementEvents) {
        $envelope = Read-XEventEnvelope ([string]$event.event_xml)
        $values = $envelope.Values
        [pscustomobject][ordered]@{
            timestamp = $envelope.Timestamp
            logical_reads = if ($values.ContainsKey('logical_reads')) { [long]$values['logical_reads'] } else { 0L }
            duration = if ($values.ContainsKey('duration')) { [long]$values['duration'] } else { 0L }
            cpu_time = if ($values.ContainsKey('cpu_time')) { [long]$values['cpu_time'] } else { 0L }
            spills = if ($values.ContainsKey('spills')) { [long]$values['spills'] } else { 0L }
            row_count = if ($values.ContainsKey('row_count')) { [long]$values['row_count'] } else { 0L }
            statement = if ($values.ContainsKey('statement')) { [string]$values['statement'] } else { '' }
        }
    }

    $planSummaries = foreach ($event in $planEvents) {
        $envelope = Read-XEventEnvelope ([string]$event.event_xml)
        $values = $envelope.Values
        $planText = if ($values.ContainsKey('showplan_xml')) { [string]$values['showplan_xml'] } else { '' }
        [pscustomobject][ordered]@{
            timestamp = $envelope.Timestamp
            memoryGrantCaptured = $planText.IndexOf('<MemoryGrantInfo', [StringComparison]::OrdinalIgnoreCase) -ge 0
            granted_memory_kb = if ($values.ContainsKey('granted_memory_kb')) { [long]$values['granted_memory_kb'] } else { 0L }
            requested_memory_kb = if ($values.ContainsKey('requested_memory_kb')) { [long]$values['requested_memory_kb'] } else { 0L }
            used_memory_kb = if ($values.ContainsKey('used_memory_kb')) { [long]$values['used_memory_kb'] } else { 0L }
            spillToTempDb = $planText.IndexOf('SpillToTempDb', [StringComparison]::OrdinalIgnoreCase) -ge 0
            planSha256 = Get-Sha256String $planText
            runtimeIo = @(Read-ShowPlanRuntimeIo $planText)
            planXml = $planText
        }
    }

    $persistedHistoricalBoundary = @(Invoke-SqlTable $databaseConnectionString @"
SELECT CONVERT(nvarchar(36), HistoryEpoch) AS historyEpoch,
       EarliestAvailableHostUtc AS earliestAvailableHostUtc
FROM mesingest.SchemaInfo
WHERE Id = 1;
"@ @{}) | Select-Object -First 1

    $queryEvidence = New-Object System.Collections.ArrayList
    $surfaceNames = @($surfaces | ForEach-Object { [string]$_.name })
    if ($measureRawEvidence) { $surfaceNames += 'RawEvidence' }
    foreach ($surfaceName in $surfaceNames) {
        $surfaceSamples = @($measurements | Where-Object { $_.name -eq $surfaceName })
        if ($surfaceSamples.Count -ne $MeasurementCount) { throw "Missing measurement samples for $surfaceName." }
        $sampleWindows = @($surfaceSamples | ForEach-Object {
            [pscustomobject]@{
                Start = [DateTimeOffset]::Parse([string]$_.startedAt).ToUniversalTime()
                End = [DateTimeOffset]::Parse([string]$_.completedAt).ToUniversalTime()
            }
        })
        $surfaceStatements = @($statementMetrics | Where-Object {
            $eventTime = $_.timestamp
            @($sampleWindows | Where-Object { $eventTime -ge $_.Start -and $eventTime -le $_.End }).Count -gt 0
        })
        $surfacePlans = @($planSummaries | Where-Object {
            $eventTime = $_.timestamp
            @($sampleWindows | Where-Object { $eventTime -ge $_.Start -and $eventTime -le $_.End }).Count -gt 0
        })
        $surfaceRuntimeIo = @($surfacePlans | ForEach-Object { @($_.runtimeIo) })
        $rawObservationRuntimeIo = @($surfaceRuntimeIo | Where-Object {
            ([string]$_.table).Trim('[', ']') -eq 'DemandRawObservations'
        })
        $objectAccessRuntimeIo = @($surfaceRuntimeIo | Where-Object {
            [string]$_.physicalOperation -match '(Scan|Seek|Lookup)'
        })
        $pollTraceObjectAccess = @($objectAccessRuntimeIo | Where-Object {
            ([string]$_.table).Trim('[', ']') -eq 'PollTraces'
        })
        $rawObservationObjectAccess = @($objectAccessRuntimeIo | Where-Object {
            ([string]$_.table).Trim('[', ']') -eq 'DemandRawObservations'
        })
        $pollTraceObjectKeySeekCount = @($pollTraceObjectAccess | Where-Object {
            [string]$_.physicalOperation -match 'Seek' -and
            ([string]$_.index).Trim('[', ']') -eq 'PK_MesIngest_PollTraces'
        }).Count
        $rawEvidenceObjectKeySeekCount = @($rawObservationObjectAccess | Where-Object {
            [string]$_.physicalOperation -match 'Seek' -and
            ([string]$_.index).Trim('[', ']') -eq 'PK_MesIngest_DemandRawObservations'
        }).Count
        $unrelatedHistoryScanCount = @(
            @($pollTraceObjectAccess) + @($rawObservationObjectAccess) |
                Where-Object { [string]$_.physicalOperation -match 'Scan' }
        ).Count
        $objectKeySeekComplete = if ($surfaceName -eq 'PollTrace') {
            $pollTraceObjectKeySeekCount -gt 0 -and $rawEvidenceObjectKeySeekCount -gt 0 -and
                $unrelatedHistoryScanCount -eq 0
        } elseif ($surfaceName -eq 'RawEvidence') {
            $rawEvidenceObjectKeySeekCount -gt 0 -and $unrelatedHistoryScanCount -eq 0
        } else { $true }
        $earliestIdentityComplete = $true
        if ($surfaceName -eq 'PollTrace') {
            $boundaryIdentities = @($surfaceSamples | ForEach-Object {
                $body = $_.body | ConvertFrom-Json
                if ($null -eq $body.historyEpoch -or $null -eq $body.earliestAvailableHostUtc) {
                    return $null
                }
                ([guid]$body.historyEpoch).ToString('D') + '|' +
                    ([DateTimeOffset]$body.earliestAvailableHostUtc).ToUniversalTime().ToString('O')
            } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
            $persistedBoundaryIdentity = ([guid]$persistedHistoricalBoundary.historyEpoch).ToString('D') + '|' +
                ([DateTimeOffset]$persistedHistoricalBoundary.earliestAvailableHostUtc).ToUniversalTime().ToString('O')
            $earliestIdentityComplete = $boundaryIdentities.Count -eq $surfaceSamples.Count -and
                @($boundaryIdentities | Select-Object -Unique).Count -eq 1 -and
                $boundaryIdentities[0] -eq $persistedBoundaryIdentity
        }
        $rawObservationLogicalReads = Get-TotalActualLogicalReads $rawObservationRuntimeIo
        $runtimeIoCarriers = @($surfaceRuntimeIo | Where-Object {
            [string]$_.physicalOperation -match '(Scan|Seek|Lookup)'
        })
        $sortedLatency = @($surfaceSamples | ForEach-Object { [double]$_.latencyMs } | Sort-Object)
        [void]$queryEvidence.Add([pscustomobject][ordered]@{
            name = $surfaceName
            samples = $surfaceSamples.Count
            responseBytes = [long](($surfaceSamples | Measure-Object -Property responseBytes -Maximum).Maximum)
            maxResponseBytes = if ($historicalObjectMaxResponseBytes.ContainsKey($surfaceName)) {
                [long]$historicalObjectMaxResponseBytes[$surfaceName]
            } else { 0L }
            p50LatencyMs = Get-NearestRankPercentile $sortedLatency 0.50
            p95LatencyMs = Get-NearestRankPercentile $sortedLatency 0.95
            p99LatencyMs = Get-NearestRankPercentile $sortedLatency 0.99
            statisticsSource = 'sql_statement_completed plus actual-plan per-object runtime IO/TIME counters'
            actualPlanSource = 'query_post_execution_showplan'
            statementCount = $surfaceStatements.Count
            actualPlanCount = $surfacePlans.Count
            runtimeIoComplete = $runtimeIoCarriers.Count -gt 0 -and
                @($runtimeIoCarriers | Where-Object { -not [bool]$_.actualLogicalReadsPresent }).Count -eq 0
            memoryGrantEvidenceComplete = $surfacePlans.Count -gt 0 -and
                @($surfacePlans | Where-Object { -not [bool]$_.memoryGrantCaptured }).Count -eq 0
            logicalReads = if ($historicalObjectSurfaceFailurePrefixes.ContainsKey($surfaceName)) {
                Get-TotalActualLogicalReads $surfaceRuntimeIo
            } elseif ($surfaceStatements.Count -gt 0) {
                Get-LongPropertySum @($surfaceStatements) 'logical_reads'
            } else {
                Get-TotalActualLogicalReads $surfaceRuntimeIo
            }
            durationMicroseconds = Get-LongPropertySum @($surfaceStatements) 'duration'
            cpuMicroseconds = Get-LongPropertySum @($surfaceStatements) 'cpu_time'
            maxGrantedMemoryKb = Get-LongPropertyMaximum @($surfacePlans) 'granted_memory_kb'
            spillCount = (Get-LongPropertySum @($surfaceStatements) 'spills') +
                @($surfacePlans | Where-Object { $_.spillToTempDb }).Count
            rawObservationPlanOperators = $rawObservationRuntimeIo.Count
            rawObservationLogicalReads = $rawObservationLogicalReads
            pollTraceObjectKeySeekCount = $pollTraceObjectKeySeekCount
            rawEvidenceObjectKeySeekCount = $rawEvidenceObjectKeySeekCount
            unrelatedHistoryScanCount = $unrelatedHistoryScanCount
            objectKeySeekComplete = $objectKeySeekComplete
            earliestIdentityComplete = $earliestIdentityComplete
            planSha256 = @($surfacePlans | Select-Object -ExpandProperty planSha256 -Unique)
        })
    }

    $storage = Get-StorageSnapshot $databaseConnectionString
    if ($FastCapacityProjection) {
        [void]$capacityResourceSnapshots.Add((Get-CapacityResourceSnapshot `
            $masterConnectionString $databaseConnectionString $DatabaseName 'after-query-evidence'))
    }

    if ($FastCapacityProjection -and $historyRoundCount -gt 0) {
        $cleanupDefaultPath = Join-Path $ServiceRoot 'appsettings.json'
        if (-not (Test-Path -LiteralPath $cleanupDefaultPath -PathType Leaf)) {
            throw "Published cleanup defaults are missing: $cleanupDefaultPath"
        }
        $publishedDefaults = (Get-Content -Raw -LiteralPath $cleanupDefaultPath | ConvertFrom-Json).MesIngest
        $activeBefore = Get-ActiveSeriesGraphSnapshot $databaseConnectionString
        $cleanupAt = [DateTimeOffset]::UtcNow
        [void](Invoke-SqlNonQuery $databaseConnectionString @"
UPDATE mesingest.PollTraces
SET StartedAt = DATEADD(day, -31, @cleanupAt),
    CompletedAt = DATEADD(day, -31, DATEADD(second, 1, @cleanupAt));

;WITH eligible AS
(
    SELECT TOP (25) series.SeriesId
    FROM mesingest.DemandSeries AS series
    WHERE series.Lifecycle = N'ARCHIVED'
      AND NOT EXISTS
      (
          SELECT 1 FROM mesingest.DemandSeriesCurrentConditions AS condition
          WHERE condition.SeriesId = series.SeriesId
      )
    ORDER BY series.SeriesId
)
UPDATE series
SET RetentionEligibilityAt = DATEADD(day, -31, @cleanupAt)
FROM mesingest.DemandSeries AS series
INNER JOIN eligible ON eligible.SeriesId = series.SeriesId;
"@ @{ '@cleanupAt' = $cleanupAt } 300)

        $cleanupHost = Start-EvidenceHost `
            $databaseConnectionString $secret "MesIngest.ScaleEvidence.$runId.cleanup" 1
        try {
            $cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(150)
            do {
                Start-Sleep -Milliseconds 500
                $cleanupState = @(Invoke-SqlTable $databaseConnectionString @"
SELECT HistoryCleanupStatus AS status,
       HistoryCleanupRunId AS runId,
       HistoryCleanupTotalExpiredPollTraceCount AS totalExpiredPollTraceCount,
       HistoryCleanupTotalDeletedRawObservationCount AS totalDeletedRawObservationCount,
       HistoryCleanupTotalDeletedSeriesCount AS totalDeletedSeriesCount,
       HistoryCleanupLastFailureCode AS lastFailureCode,
       HistoryCleanupLastFailureReason AS lastFailureReason
FROM mesingest.HistoryCleanupState WHERE Id = 1;
"@ @{}) | Select-Object -First 1
                if ($null -ne $cleanupState -and
                    [long]$cleanupState.totalDeletedRawObservationCount -ge $expectedTotalObservationCount -and
                    [long]$cleanupState.totalDeletedSeriesCount -ge 25) {
                    break
                }
            } while ([DateTimeOffset]::UtcNow -lt $cleanupDeadline)
        } finally {
            Stop-EvidenceHost $cleanupHost
        }

        $activeAfter = Get-ActiveSeriesGraphSnapshot $databaseConnectionString
        $tombstones = @(Invoke-SqlTable $databaseConnectionString @"
SELECT COUNT_BIG(*) AS tombstoneCount
FROM mesingest.ArchivedDemandKeyTombstones;
"@ @{}) | Select-Object -First 1
        $remaining = @(Invoke-SqlTable $databaseConnectionString @"
SELECT (SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations) AS rawObservationCount,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandSeries WHERE RetentionEligibilityAt IS NOT NULL) AS retentionEligibleSeriesCount;
"@ @{}) | Select-Object -First 1
        $storageAfterCleanup = Get-StorageSnapshot $databaseConnectionString
        [void]$capacityResourceSnapshots.Add((Get-CapacityResourceSnapshot `
            $masterConnectionString $databaseConnectionString $DatabaseName 'after-cleanup'))

        $activeGraphUnchanged = [long]$activeBefore.splitSeriesCount -eq 0 -and
            [long]$activeAfter.splitSeriesCount -eq 0 -and
            [long]$activeBefore.seriesCount -eq [long]$activeAfter.seriesCount -and
            [long]$activeBefore.demandCount -eq [long]$activeAfter.demandCount -and
            [long]$activeBefore.eventCount -eq [long]$activeAfter.eventCount -and
            [long]$activeBefore.conditionCount -eq [long]$activeAfter.conditionCount -and
            [long]$activeBefore.errorPeriodCount -eq [long]$activeAfter.errorPeriodCount -and
            [long]$activeBefore.errorEvidenceCount -eq [long]$activeAfter.errorEvidenceCount -and
            [long]$activeBefore.graphFactCount -eq [long]$activeAfter.graphFactCount -and
            [string]$activeBefore.graphIdentitySha256 -ceq [string]$activeAfter.graphIdentitySha256
        $cleanupEvidence = [pscustomobject][ordered]@{
            acceleratedCheckIntervalSeconds = 1
            defaultCheckIntervalSeconds = [int]$publishedDefaults.HistoryCleanupCheckIntervalSeconds
            defaultRawObservationBatch = [int]$publishedDefaults.HistoryCleanupMaximumRawObservationRowsPerBatch
            defaultSeriesBatch = [int]$publishedDefaults.HistoryCleanupMaximumSeriesPerBatch
            defaultTimeBudgetSeconds = [int]$publishedDefaults.HistoryCleanupTimeBudgetSeconds
            expectedRawObservationRows = $expectedTotalObservationCount
            deletedRawObservationRows = if ($null -eq $cleanupState) { -1L } else { [long]$cleanupState.totalDeletedRawObservationCount }
            remainingRawObservationRows = [long]$remaining.rawObservationCount
            expectedEligibleSeries = 25
            deletedEligibleSeries = if ($null -eq $cleanupState) { -1L } else { [long]$cleanupState.totalDeletedSeriesCount }
            remainingRetentionEligibleSeries = [long]$remaining.retentionEligibleSeriesCount
            tombstonesWritten = [long]$tombstones.tombstoneCount
            activeSeriesWholeBefore = [long]$activeBefore.seriesCount
            activeSeriesWholeAfter = if ($activeGraphUnchanged) { [long]$activeAfter.seriesCount } else { -1L }
            activeGraphUnchanged = $activeGraphUnchanged
            activeGraphBefore = $activeBefore
            activeGraphAfter = $activeAfter
            finalState = $cleanupState
            storageAfterCleanup = $storageAfterCleanup
        }
    }

    if ($AcceleratedConcurrencyStability) {
        $stabilityStartedAt = [DateTimeOffset]::UtcNow
        try {
        $capacityBlocker = Get-CapacityBlockerEvidence $CapacityBlockerEvidencePath
        if (-not [bool]$capacityBlocker.preserved) {
            throw 'Ticket 27 capacity blocker evidence is missing or invalid.'
        }
        $publishedDefaultsPath = Join-Path $ServiceRoot 'appsettings.json'
        if (-not (Test-Path -LiteralPath $publishedDefaultsPath -PathType Leaf)) {
            throw "Published Host defaults are missing: $publishedDefaultsPath"
        }
        $publishedDefaults = (Get-Content -Raw -LiteralPath $publishedDefaultsPath | ConvertFrom-Json).MesIngest
        $deterministicContract = Get-DeterministicContractEvidence `
            $DeterministicContractEvidencePath $sourceCommit $hostSha256
        $recordingPath = Join-Path $PSScriptRoot 'release-smoke-rounds.json'
        if (-not (Test-Path -LiteralPath $recordingPath -PathType Leaf)) {
            throw "Packaged scripted MesTaskUnionRound recording is missing: $recordingPath"
        }

        $stabilityDeadline = $stabilityStartedAt.AddMinutes($StabilityDurationMinutes)
        $restartAt = $stabilityStartedAt.AddSeconds(($StabilityDurationMinutes * 60) / 2.0)
        $nextResourceAt = $stabilityStartedAt
        $nextProgressAt = $stabilityStartedAt.AddMinutes(1)
        $restartCount = 0L
        $batchCount = 0L
        $frozenDetailReads = 0L
        $frozenCommitMismatchCount = 0L
        $projectionCommitsDuringFrozenReads = 0L
        $frozenWindowsWithoutProjection = 0L
        $catalogReads = 0L
        $watchReads = 0L
        $packagedWatchClientReads = 0L
        $packagedReferenceConsumerReads = 0L
        $packagedClientReceipts = New-Object System.Collections.ArrayList
        $packageRoot = Split-Path -Parent $ServiceRoot
        $forbiddenTokenPath = Join-Path $runDirectory 'ticket28-forbidden-key-tokens.empty.txt'
        $catalogETag = ''
        $restartStatePreserved = $true
        $epochBeforeRestart = [string]$schemaIdentity.historyEpoch
        $pollCountBeforeRestart = 0L

        # Make the fixed representative sample old through the existing retention seam.
        # The accelerated profile changes operation counts only; the 30-day policy stays intact.
        [void](Invoke-SqlNonQuery $databaseConnectionString @"
UPDATE mesingest.PollTraces
SET StartedAt = DATEADD(day, -31, StartedAt),
    CompletedAt = DATEADD(day, -31, CompletedAt)
WHERE PollTraceId LIKE N'scale-%';
UPDATE mesingest.SchemaInfo
SET EarliestAvailableHostUtc = (SELECT MIN(CompletedAt) FROM mesingest.PollTraces)
WHERE Id = 1;
"@ @{} 300)
        $earliestBefore = [DateTimeOffset](@(Invoke-SqlTable $databaseConnectionString `
            'SELECT EarliestAvailableHostUtc FROM mesingest.SchemaInfo WHERE Id = 1;' @{}) |
            Select-Object -First 1).EarliestAvailableHostUtc

        $stabilityXEventSession = ('MesIngestStability_' + $runId.Replace('-', '_'))
        $stabilityXelBase = Join-Path $errorLogDirectory ($stabilityXEventSession + '.xel')
        $escapedStabilitySession = $stabilityXEventSession.Replace(']', ']]')
        $escapedStabilityXel = $stabilityXelBase.Replace("'", "''")
        [void](Invoke-SqlNonQuery $masterConnectionString @"
CREATE EVENT SESSION [$escapedStabilitySession] ON SERVER
ADD EVENT sqlserver.error_reported
(
    ACTION(sqlserver.client_app_name)
    WHERE (error_number = 701)
),
ADD EVENT sqlserver.sort_warning(ACTION(sqlserver.client_app_name)),
ADD EVENT sqlserver.hash_warning(ACTION(sqlserver.client_app_name))
ADD TARGET package0.event_file(SET filename = N'$escapedStabilityXel', max_file_size = 64, max_rollover_files = 4)
WITH (MAX_MEMORY = 4096 KB, EVENT_RETENTION_MODE = NO_EVENT_LOSS,
      MAX_DISPATCH_LATENCY = 1 SECONDS, TRACK_CAUSALITY = OFF, STARTUP_STATE = OFF);
ALTER EVENT SESSION [$escapedStabilitySession] ON SERVER STATE = START;
"@)
        $stabilityXEventStarted = $true

        $stabilityAppName = "MesIngest.ScaleEvidence.$runId.stability"
        $hostRun = Start-EvidenceHost `
            $databaseConnectionString $secret $stabilityAppName `
            -CleanupCheckIntervalSeconds $AcceleratedCleanupCheckIntervalSeconds `
            -PollStartIntervalSeconds $AcceleratedPollStartIntervalSeconds `
            -ReplayRecordingPath $recordingPath `
            -ContinuousPoll
        $watchProbePath = Join-Path $runDirectory 'packaged-watch-probe-initial.json'
        $watchProbe = Invoke-PackagedWatchProbe `
            $packageRoot $hostRun.BaseUrl $secret $watchProbePath
        $packagedWatchClientReads += [long]$watchProbe.readCount
        [void]$packagedClientReceipts.Add([pscustomobject][ordered]@{
            kind = 'Watch'; phase = 'initial'; file = [IO.Path]::GetFileName($watchProbePath)
            sha256 = Get-FileSha256 $watchProbePath; readCount = [long]$watchProbe.readCount
        })
        $referenceProbe = Invoke-PackagedReferenceConsumerProbe `
            $packageRoot $hostRun.BaseUrl $secret ([string]$schemaIdentity.historyEpoch) $forbiddenTokenPath
        $referenceProbePath = Join-Path $runDirectory 'packaged-reference-consumer-initial.json'
        $referenceProbe | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $referenceProbePath -Encoding UTF8
        $packagedReferenceConsumerReads++
        [void]$packagedClientReceipts.Add([pscustomobject][ordered]@{
            kind = 'ReferenceConsumer'; phase = 'initial'; file = [IO.Path]::GetFileName($referenceProbePath)
            sha256 = Get-FileSha256 $referenceProbePath; readCount = 1
        })
        $client = [Net.Http.HttpClient]::new()
        try {
            $client.Timeout = [TimeSpan]::FromSeconds($RequestTimeoutSeconds)
            $client.DefaultRequestHeaders.Authorization =
                [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $secret)
            while ([DateTimeOffset]::UtcNow -lt $stabilityDeadline) {
                $now = [DateTimeOffset]::UtcNow
                if ($restartCount -eq 0 -and $now -ge $restartAt) {
                    $beforeRestart = @(Invoke-SqlTable $databaseConnectionString @"
SELECT CONVERT(nvarchar(36), HistoryEpoch) AS historyEpoch,
       (SELECT COUNT_BIG(*) FROM mesingest.PollTraces) AS pollTraceCount
FROM mesingest.SchemaInfo WHERE Id = 1;
"@ @{}) | Select-Object -First 1
                    $pollCountBeforeRestart = [long]$beforeRestart.pollTraceCount
                    Stop-EvidenceHost $hostRun
                    $hostRun = $null
                    $hostRun = Start-EvidenceHost `
                        $databaseConnectionString $secret $stabilityAppName `
                        -CleanupCheckIntervalSeconds $AcceleratedCleanupCheckIntervalSeconds `
                        -PollStartIntervalSeconds $AcceleratedPollStartIntervalSeconds `
                        -ReplayRecordingPath $recordingPath `
                        -ContinuousPoll
                    $afterRestart = @(Invoke-SqlTable $databaseConnectionString @"
SELECT CONVERT(nvarchar(36), HistoryEpoch) AS historyEpoch,
       (SELECT COUNT_BIG(*) FROM mesingest.PollTraces) AS pollTraceCount
FROM mesingest.SchemaInfo WHERE Id = 1;
"@ @{}) | Select-Object -First 1
                    $restartStatePreserved = [string]$beforeRestart.historyEpoch -ceq [string]$afterRestart.historyEpoch -and
                        [string]$afterRestart.historyEpoch -ceq $epochBeforeRestart -and
                        [long]$afterRestart.pollTraceCount -ge $pollCountBeforeRestart
                    $restartCount++
                    $catalogETag = ''
                    $watchProbePath = Join-Path $runDirectory 'packaged-watch-probe-after-restart.json'
                    $watchProbe = Invoke-PackagedWatchProbe `
                        $packageRoot $hostRun.BaseUrl $secret $watchProbePath
                    $packagedWatchClientReads += [long]$watchProbe.readCount
                    [void]$packagedClientReceipts.Add([pscustomobject][ordered]@{
                        kind = 'Watch'; phase = 'after-restart'; file = [IO.Path]::GetFileName($watchProbePath)
                        sha256 = Get-FileSha256 $watchProbePath; readCount = [long]$watchProbe.readCount
                    })
                    $referenceProbe = Invoke-PackagedReferenceConsumerProbe `
                        $packageRoot $hostRun.BaseUrl $secret ([string]$schemaIdentity.historyEpoch) $forbiddenTokenPath
                    $referenceProbePath = Join-Path $runDirectory 'packaged-reference-consumer-after-restart.json'
                    $referenceProbe | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $referenceProbePath -Encoding UTF8
                    $packagedReferenceConsumerReads++
                    [void]$packagedClientReceipts.Add([pscustomobject][ordered]@{
                        kind = 'ReferenceConsumer'; phase = 'after-restart'; file = [IO.Path]::GetFileName($referenceProbePath)
                        sha256 = Get-FileSha256 $referenceProbePath; readCount = 1
                    })
                }

                $batch = Invoke-StabilityHttpBatch `
                    -Client $client -BaseUrl $hostRun.BaseUrl `
                    -DatabaseConnectionString $databaseConnectionString `
                    -CatalogETag $catalogETag -BatchNumber $batchCount
                $catalogETag = [string]$batch.catalogETag
                foreach ($sample in @($batch.samples)) {
                    [void]$stabilityLatencySamples.Add($sample)
                    if ([string]$sample.name -eq 'ExternallyReadableDemandCatalog') { $catalogReads++ }
                    elseif ([string]$sample.name -eq 'DemandSeriesFrozenDetail') { }
                    else { $watchReads++ }
                }
                $frozenDetailReads += [long]$batch.frozenReadCount
                $frozenCommitMismatchCount += [long]$batch.frozenMismatchCount
                $projectionCommitsDuringFrozenReads += [long]$batch.projectionCommitsDuringFrozenReads
                $frozenWindowsWithoutProjection += [long]$batch.frozenWindowWithoutProjection
                $batchCount++

                $now = [DateTimeOffset]::UtcNow
                if ($now -ge $nextResourceAt) {
                    [void]$stabilityResourceSnapshots.Add((Get-StabilityResourceSnapshot `
                        $masterConnectionString $databaseConnectionString $DatabaseName `
                        $hostRun ([int]$serverIdentity.process_id) "minute-$($stabilityResourceSnapshots.Count)"))
                    $nextResourceAt = $nextResourceAt.AddMinutes(1)
                }
                if ($now -ge $nextProgressAt) {
                    Write-Output ("MESINGEST_STABILITY_PROGRESS: elapsedMinutes={0:F1} batches={1} apiReads={2} frozenReads={3}" -f `
                        ($now - $stabilityStartedAt).TotalMinutes, $batchCount,
                        $stabilityLatencySamples.Count, $frozenDetailReads)
                    $nextProgressAt = $nextProgressAt.AddMinutes(1)
                }
                Start-Sleep -Milliseconds 250
            }
            [void]$stabilityResourceSnapshots.Add((Get-StabilityResourceSnapshot `
                $masterConnectionString $databaseConnectionString $DatabaseName `
                $hostRun ([int]$serverIdentity.process_id) 'final'))
        } finally {
            $client.Dispose()
            Stop-EvidenceHost $hostRun
            $hostRun = $null
        }
        $stabilityCompletedAt = [DateTimeOffset]::UtcNow
        [void](Invoke-SqlNonQuery $masterConnectionString `
            "ALTER EVENT SESSION [$escapedStabilitySession] ON SERVER STATE = STOP;")
        $stabilityXEventStarted = $false
        $stabilityXelPattern = $stabilityXelBase.Substring(0, $stabilityXelBase.Length - 4) + '*.xel'
        $stabilityEvents = @(Invoke-SqlTable $masterConnectionString @"
SELECT object_name, CONVERT(nvarchar(max), event_data) AS event_xml
FROM sys.fn_xe_file_target_read_file(@xelPattern, NULL, NULL, NULL);
"@ @{ '@xelPattern' = $stabilityXelPattern } 300)
        $relevantStabilityEvents = @($stabilityEvents | Where-Object {
            ([string]$_.event_xml).IndexOf($stabilityAppName, [StringComparison]::Ordinal) -ge 0
        })
        $error701Count = @($relevantStabilityEvents | Where-Object { $_.object_name -eq 'error_reported' }).Count
        $spillCount = @($relevantStabilityEvents | Where-Object {
            $_.object_name -eq 'sort_warning' -or $_.object_name -eq 'hash_warning'
        }).Count

        $pollTiming = @(Invoke-SqlTable $databaseConnectionString @"
;WITH stabilityPolls AS
(
    SELECT PollTraceSequence, StartedAt, CompletedAt, Outcome,
           LAG(StartedAt) OVER (ORDER BY PollTraceSequence) AS previousStartedAt
    FROM mesingest.PollTraces
    WHERE StartedAt >= @stabilityStartedAt
)
SELECT COUNT_BIG(*) AS pollCount,
       SUM(CASE WHEN Outcome = N'SUCCESS' THEN 1 ELSE 0 END) AS successfulPollCount,
       SUM(CASE WHEN previousStartedAt IS NOT NULL
                     AND DATEDIFF_BIG(millisecond, previousStartedAt, StartedAt) < @catchUpThresholdMs
                THEN 1 ELSE 0 END) AS catchUpBurstCount,
       (SELECT COUNT_BIG(*)
        FROM stabilityPolls AS earlier
        INNER JOIN stabilityPolls AS later
          ON earlier.PollTraceSequence < later.PollTraceSequence
         AND earlier.CompletedAt > later.StartedAt) AS overlappingPollCount
FROM stabilityPolls;
"@ @{
            '@stabilityStartedAt' = $stabilityStartedAt
            '@catchUpThresholdMs' = [long]($AcceleratedPollStartIntervalSeconds * 500)
        }) | Select-Object -First 1
        $cleanupFinal = @(Invoke-SqlTable $databaseConnectionString @"
SELECT schemaInfo.EarliestAvailableHostUtc AS earliestAvailableHostUtc,
       pressure.StoragePressureStatus AS storagePressureStatus,
       cleanup.HistoryCleanupStatus AS cleanupStatus,
       cleanup.HistoryCleanupTotalExpiredPollTraceCount AS expiredPollTraceCount,
       cleanup.HistoryCleanupTotalDeletedRawObservationCount AS deletedRawObservationCount,
       cleanup.HistoryCleanupTotalDeletedSeriesCount AS deletedSeriesCount,
       cleanup.HistoryCleanupLastFailureCode AS cleanupFailureCode,
       (SELECT COUNT_BIG(*) FROM mesingest.PollTraces AS poll
        WHERE poll.CompletedAt <= DATEADD(day, -30, SYSUTCDATETIME())
          AND EXISTS (SELECT 1 FROM mesingest.DemandRawObservations AS raw
                      WHERE raw.PollTraceId = poll.PollTraceId)) AS cleanupBacklogCount,
       (SELECT COUNT_BIG(*) FROM mesingest.DemandRawObservations) AS finalRawObservationRows,
       (SELECT COUNT_BIG(*) FROM mesingest.ProjectionCommits
        WHERE CommittedAt >= @stabilityStartedAt) AS projectionCommitsDuringFrozenReads
FROM mesingest.SchemaInfo AS schemaInfo
CROSS JOIN mesingest.StoragePressureState AS pressure
CROSS JOIN mesingest.HistoryCleanupState AS cleanup
WHERE schemaInfo.Id = 1 AND pressure.Id = 1 AND cleanup.Id = 1;
"@ @{ '@stabilityStartedAt' = $stabilityStartedAt }) | Select-Object -First 1

        $latencies = @($stabilityLatencySamples | ForEach-Object { [double]$_.latencyMs } | Sort-Object)
        $quarterCount = [Math]::Max(1, [int][Math]::Floor($latencies.Count / 4))
        $firstQuarter = @($stabilityLatencySamples | Select-Object -First $quarterCount |
            ForEach-Object { [double]$_.latencyMs } | Sort-Object)
        $lastQuarter = @($stabilityLatencySamples | Select-Object -Last $quarterCount |
            ForEach-Object { [double]$_.latencyMs } | Sort-Object)
        $resourceSnapshots = @($stabilityResourceSnapshots)
        $resourceSemaphoreSustainedSamples = 0L
        $resourceWaitBaseline = if ($resourceSnapshots.Count -eq 0) { 0L } else {
            [long]$resourceSnapshots[0].resourceSemaphoreWaitingTasks
        }
        foreach ($snapshot in $resourceSnapshots) {
            if ([long]$snapshot.pendingMemoryGrants -gt 0 -or
                [long]$snapshot.memoryGrantsPending -gt 0 -or
                [long]$snapshot.resourceSemaphoreWaitingTasks -gt $resourceWaitBaseline) {
                $resourceSemaphoreSustainedSamples++
            }
        }
        $maximumLockWaitMs = if ($resourceSnapshots.Count -eq 0) { [double]::PositiveInfinity } else {
            [double](($resourceSnapshots | Measure-Object -Property maximumLockWaitMs -Maximum).Maximum)
        }
        $unboundedLockWaitCount = @($resourceSnapshots | Where-Object {
            [long]$_.blockedRequestCount -gt 0 -and [long]$_.maximumLockWaitMs -gt 5000
        }).Count
        $maximumPendingMemoryGrants = if ($resourceSnapshots.Count -eq 0) { -1L } else {
            [long](($resourceSnapshots | Measure-Object -Property pendingMemoryGrants -Maximum).Maximum)
        }
        $storagePressurePauseCount = @($resourceSnapshots | Where-Object {
            [string]$_.storagePressureStatus -like 'PAUSED*'
        }).Count
        $earliestAfter = [DateTimeOffset]$cleanupFinal.earliestAvailableHostUtc
        $actualDurationSeconds = ($stabilityCompletedAt - $stabilityStartedAt).TotalSeconds
        $stabilityEvidence = [pscustomobject][ordered]@{
            identity = [pscustomobject][ordered]@{
                sourceCommit = $sourceCommit
                hostSha256 = $hostSha256
                contractVersion = [string]$schemaIdentity.contractVersion
                schemaVersion = [int]$schemaIdentity.schemaVersion
                historyEpoch = [string]$schemaIdentity.historyEpoch
            }
            environment = [pscustomobject][ordered]@{
                sqlProductMajor = [int]$serverIdentity.product_major
                compatibilityLevel = [int]$databaseConfiguration.compatibilityLevel
                maxServerMemoryMb = [int]$serverIdentity.max_server_memory_mb
                recoveryModel = [string]$databaseConfiguration.recoveryModel
                defaultPollStartIntervalSeconds = [int]$publishedDefaults.PollStartIntervalSeconds
                failureBackoffSeconds = @(60, 120, 300)
                watchRefreshSeconds = [pscustomobject][ordered]@{
                    overview = 30; currentAttention = 30; demandSeries = 60
                    readabilityAudit = 60; errorSearch = 60
                }
                defaultCleanupCheckIntervalSeconds = [int]$publishedDefaults.HistoryCleanupCheckIntervalSeconds
                acceleratedPollStartIntervalSeconds = $AcceleratedPollStartIntervalSeconds
                acceleratedCleanupCheckIntervalSeconds = $AcceleratedCleanupCheckIntervalSeconds
            }
            workload = [pscustomobject][ordered]@{
                durationSeconds = $actualDurationSeconds
                seriesCount = $SeriesCount
                initialRawObservationRows = $expectedTotalObservationCount
                maximumRawObservationRows = [long](($resourceSnapshots | Measure-Object -Property rawObservationCount -Maximum).Maximum)
                representativeHistoryRounds = $RepresentativeHistoryRounds
                concurrentClients = 8
                acceleration = [pscustomobject][ordered]@{
                    pollMultiplier = 60.0 / $AcceleratedPollStartIntervalSeconds
                    cleanupMultiplier = 3600.0 / $AcceleratedCleanupCheckIntervalSeconds
                }
                operations = [pscustomobject][ordered]@{
                    successfulPolls = [long]$pollTiming.successfulPollCount
                    totalPolls = [long]$pollTiming.pollCount
                    watchApiReads = $watchReads
                    frozenDetailReads = $frozenDetailReads
                    referenceCatalogReads = $catalogReads
                    packagedWatchClientReads = $packagedWatchClientReads
                    packagedReferenceConsumerReads = $packagedReferenceConsumerReads
                    cleanupChecks = [long][Math]::Floor($actualDurationSeconds / $AcceleratedCleanupCheckIntervalSeconds)
                    hostRestarts = $restartCount
                }
            }
            latency = [pscustomobject][ordered]@{
                sampleCount = $latencies.Count
                p95LatencyMs = Get-NearestRankPercentile $latencies 0.95
                p99LatencyMs = Get-NearestRankPercentile $latencies 0.99
                firstQuartileP95LatencyMs = Get-NearestRankPercentile $firstQuarter 0.95
                lastQuartileP95LatencyMs = Get-NearestRankPercentile $lastQuarter 0.95
            }
            resources = [pscustomobject][ordered]@{
                snapshotCount = $resourceSnapshots.Count
                error701Count = $error701Count
                resourceSemaphoreSustainedSamples = $resourceSemaphoreSustainedSamples
                spillCount = $spillCount
                maximumLockWaitMs = $maximumLockWaitMs
                unboundedLockWaitCount = $unboundedLockWaitCount
                maximumPendingMemoryGrants = $maximumPendingMemoryGrants
                hostWorkingSetSlopeMbPerMinute = Get-FirstLastSlope $resourceSnapshots 'hostWorkingSetMb'
                sqlWorkingSetSlopeMbPerMinute = Get-FirstLastSlope $resourceSnapshots 'sqlWorkingSetMb'
                hostWorkingSetPeakMb = [double](($resourceSnapshots | Measure-Object -Property hostWorkingSetMb -Maximum).Maximum)
                sqlWorkingSetPeakMb = [double](($resourceSnapshots | Measure-Object -Property sqlWorkingSetMb -Maximum).Maximum)
                hostHandlePeak = [long](($resourceSnapshots | Measure-Object -Property hostHandleCount -Maximum).Maximum)
                logicalDatabaseUsedSlopeMbPerMinute = Get-FirstLastSlope $resourceSnapshots 'logicalDatabaseUsedMb'
                physicalDataFileSlopeMbPerMinute = Get-FirstLastSlope $resourceSnapshots 'physicalDataFileMb'
                ldfSlopeMbPerMinute = Get-FirstLastSlope $resourceSnapshots 'ldfMb'
                tempdbUsedSlopeMbPerMinute = Get-FirstLastSlope $resourceSnapshots 'tempdbUsedMb'
                hostHandleSlopePerMinute = Get-FirstLastSlope $resourceSnapshots 'hostHandleCount'
                databaseVersionStorePeakMb = [double](($resourceSnapshots | Measure-Object -Property databaseVersionStoreMb -Maximum).Maximum)
                tempdbVersionStorePeakMb = [double](($resourceSnapshots | Measure-Object -Property tempdbVersionStoreMb -Maximum).Maximum)
                snapshots = $resourceSnapshots
            }
            behavior = [pscustomobject][ordered]@{
                maximumConcurrentPolls = if ([long]$pollTiming.overlappingPollCount -eq 0) { 1 } else { 2 }
                catchUpBurstCount = [long]$pollTiming.catchUpBurstCount
                currentLogicalReadGrowthPassed = $false
                frozenCommitMismatchCount = $frozenCommitMismatchCount
                projectionCommitsDuringFrozenReads = $projectionCommitsDuringFrozenReads
                frozenWindowsWithoutProjection = $frozenWindowsWithoutProjection
                cleanupBacklogCount = [long]$cleanupFinal.cleanupBacklogCount
                earliestAvailableAdvanced = $earliestAfter -gt $earliestBefore
                earliestAvailableBefore = $earliestBefore.ToUniversalTime().ToString('o')
                earliestAvailableAfter = $earliestAfter.ToUniversalTime().ToString('o')
                cleanupStatus = [string]$cleanupFinal.cleanupStatus
                cleanupFailureCode = if ($null -eq $cleanupFinal.cleanupFailureCode) { $null } else { [string]$cleanupFinal.cleanupFailureCode }
                expiredPollTraceCount = [long]$cleanupFinal.expiredPollTraceCount
                deletedRawObservationCount = [long]$cleanupFinal.deletedRawObservationCount
                finalRawObservationRows = [long]$cleanupFinal.finalRawObservationRows
                storagePressurePauseCount = $storagePressurePauseCount
                storagePressureStatus = [string]$cleanupFinal.storagePressureStatus
                retrySchedulePassed = [bool]$deterministicContract.retrySchedulePassed
                logicalDayBoundaryPassed = [bool]$deterministicContract.logicalDayBoundaryPassed
                historyEpochPreservedAcrossRestart = $restartStatePreserved
                restartStatePreserved = $restartStatePreserved
            }
            evidence = [pscustomobject][ordered]@{
                runtimeFailureType = $null
                resourceSnapshotsComplete = $resourceSnapshots.Count -ge ($StabilityDurationMinutes - 1)
                latencySamplesComplete = $latencies.Count -gt 0
                xeventSignalsComplete = $true
                cleanupEvidenceComplete = $null -eq $cleanupFinal.cleanupFailureCode
                deterministicContractEvidenceComplete = [bool]$deterministicContract.complete
                deterministicContract = $deterministicContract
                packagedClientReceipts = @($packagedClientReceipts)
            }
        }
        } catch {
            Stop-EvidenceHost $hostRun
            $hostRun = $null
            $runtimeFailureType = $_.Exception.GetType().Name
            $failedDurationSeconds = ([DateTimeOffset]::UtcNow - $stabilityStartedAt).TotalSeconds
            $stabilityEvidence = [pscustomobject][ordered]@{
                identity = [pscustomobject][ordered]@{
                    sourceCommit = $sourceCommit; hostSha256 = $hostSha256
                    contractVersion = [string]$schemaIdentity.contractVersion
                    schemaVersion = [int]$schemaIdentity.schemaVersion
                    historyEpoch = [string]$schemaIdentity.historyEpoch
                }
                environment = [pscustomobject][ordered]@{
                    sqlProductMajor = [int]$serverIdentity.product_major
                    compatibilityLevel = [int]$databaseConfiguration.compatibilityLevel
                    maxServerMemoryMb = [int]$serverIdentity.max_server_memory_mb
                    recoveryModel = [string]$databaseConfiguration.recoveryModel
                    defaultPollStartIntervalSeconds = 60
                    failureBackoffSeconds = @(60, 120, 300)
                    watchRefreshSeconds = [pscustomobject][ordered]@{
                        overview = 30; currentAttention = 30; demandSeries = 60
                        readabilityAudit = 60; errorSearch = 60
                    }
                    defaultCleanupCheckIntervalSeconds = 3600
                    acceleratedPollStartIntervalSeconds = $AcceleratedPollStartIntervalSeconds
                    acceleratedCleanupCheckIntervalSeconds = $AcceleratedCleanupCheckIntervalSeconds
                }
                workload = [pscustomobject][ordered]@{
                    durationSeconds = $failedDurationSeconds; seriesCount = $SeriesCount
                    initialRawObservationRows = $expectedTotalObservationCount
                    maximumRawObservationRows = $expectedTotalObservationCount
                    representativeHistoryRounds = $RepresentativeHistoryRounds
                    concurrentClients = 8
                    operations = [pscustomobject][ordered]@{
                        successfulPolls = 0; watchApiReads = 0; frozenDetailReads = 0
                        referenceCatalogReads = 0; packagedWatchClientReads = 0
                        packagedReferenceConsumerReads = 0; cleanupChecks = 0; hostRestarts = 0
                    }
                }
                latency = [pscustomobject][ordered]@{
                    sampleCount = 0; p95LatencyMs = 0.0; p99LatencyMs = 0.0
                    firstQuartileP95LatencyMs = 0.0; lastQuartileP95LatencyMs = 0.0
                }
                resources = [pscustomobject][ordered]@{
                    snapshotCount = 0; error701Count = 0; resourceSemaphoreSustainedSamples = 0
                    spillCount = 0; maximumLockWaitMs = 0.0; unboundedLockWaitCount = 0
                    maximumPendingMemoryGrants = 0; hostWorkingSetSlopeMbPerMinute = 0.0
                    sqlWorkingSetSlopeMbPerMinute = 0.0; hostWorkingSetPeakMb = 0.0
                    sqlWorkingSetPeakMb = 0.0; hostHandlePeak = 0
                    logicalDatabaseUsedSlopeMbPerMinute = 0.0; physicalDataFileSlopeMbPerMinute = 0.0
                    ldfSlopeMbPerMinute = 0.0; tempdbUsedSlopeMbPerMinute = 0.0
                    hostHandleSlopePerMinute = 0.0; databaseVersionStorePeakMb = 0.0
                    tempdbVersionStorePeakMb = 0.0; snapshots = @($stabilityResourceSnapshots)
                }
                behavior = [pscustomobject][ordered]@{
                    maximumConcurrentPolls = 0; catchUpBurstCount = 0
                    currentLogicalReadGrowthPassed = $false; frozenCommitMismatchCount = 0
                    projectionCommitsDuringFrozenReads = 0; frozenWindowsWithoutProjection = 1
                    cleanupBacklogCount = 0; earliestAvailableAdvanced = $false
                    storagePressurePauseCount = 0; retrySchedulePassed = $false
                    logicalDayBoundaryPassed = $false; historyEpochPreservedAcrossRestart = $false
                    restartStatePreserved = $false
                }
                evidence = [pscustomobject][ordered]@{
                    runtimeFailureType = $runtimeFailureType
                    resourceSnapshotsComplete = $false; latencySamplesComplete = $false
                    xeventSignalsComplete = $false; cleanupEvidenceComplete = $false
                    deterministicContractEvidenceComplete = $false
                }
            }
        }
    }

    $attestation = $null
    $tier1 = [ordered]@{
        provided = $false; attestationSchemaVersion = $null
        failed = $null; passed = $null; sqlSkippedTests = $null; total = $null
        exitCode = $null; trxVerified = $false; buildBound = $false
        baseSatisfied = $false; satisfied = $false; diagnosticCode = 'SQL_TIER1_ATTESTATION_NOT_PROVIDED'
    }
    if (-not [string]::IsNullOrWhiteSpace($SqlTier1AttestationPath) -and (Test-Path -LiteralPath $SqlTier1AttestationPath -PathType Leaf)) {
        try {
            $attestation = Get-Content -Raw -LiteralPath $SqlTier1AttestationPath | ConvertFrom-Json
            $tier1.provided = $true
            $tier1.attestationSchemaVersion = [int]$attestation.schemaVersion
            $tier1.failed = [int]$attestation.counts.failed
            $tier1.passed = [int]$attestation.counts.passed
            $tier1.sqlSkippedTests = [int]$attestation.counts.skipped
            $tier1.total = [int]$attestation.counts.total
            $tier1.exitCode = [int]$attestation.exitCode
            $attestedTrxPath = Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($SqlTier1AttestationPath))) ([string]$attestation.trxFile)
            if (Test-Path -LiteralPath $attestedTrxPath -PathType Leaf) {
                [xml]$attestedTrx = Get-Content -Raw -LiteralPath $attestedTrxPath
                $counters = $attestedTrx.TestRun.ResultSummary.Counters
                $trxTotal = [int]$counters.total
                $trxExecuted = [int]$counters.executed
                $trxPassed = [int]$counters.passed
                $trxFailed = [int]$counters.failed
                $trxSkipped = $trxTotal - $trxExecuted
                $trxAssemblies = @($attestedTrx.TestRun.TestDefinitions.UnitTest | ForEach-Object {
                    [IO.Path]::GetFileName([string]$_.storage)
                } | Sort-Object -Unique)
                $tier1.trxVerified = (Get-FileSha256 $attestedTrxPath) -ceq [string]$attestation.trxSha256 -and
                    $trxTotal -eq $tier1.total -and $trxPassed -eq $tier1.passed -and
                    $trxFailed -eq $tier1.failed -and $trxSkipped -eq $tier1.sqlSkippedTests -and
                    $trxAssemblies.Count -eq 1 -and
                    [string]::Equals($trxAssemblies[0], 'MesIngest.Tests.dll', [StringComparison]::OrdinalIgnoreCase)
            }
            $tier1.baseSatisfied = $tier1.attestationSchemaVersion -ge 2 -and
                $tier1.exitCode -eq 0 -and $tier1.failed -eq 0 -and
                $tier1.sqlSkippedTests -eq 0 -and $tier1.total -ge 700 -and $tier1.trxVerified -and
                [string]::Equals(
                    [string]$attestation.commandPattern,
                    'dotnet test MesIngest.Tests --configuration Release --results-directory <path> --logger "trx;LogFileName=runtime-feedback-tier1.trx"',
                    [StringComparison]::Ordinal) -and
                [int]$attestation.actualProductMajor -eq [int]$serverIdentity.product_major -and
                [string]::Equals([string]$attestation.sqlTarget.dataSource, $masterBuilder.DataSource, [StringComparison]::OrdinalIgnoreCase)
            $tier1.diagnosticCode = if ($tier1.baseSatisfied) { 'SQL_TIER1_AWAITING_BUILD_BINDING' } else { 'SQL_TIER1_ATTESTATION_INVALID' }
        } catch {
            $tier1.diagnosticCode = 'SQL_TIER1_ATTESTATION_INVALID'
        }
    }

    $sourceCommit = @(& git -C $ServiceRoot rev-parse HEAD 2>$null) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($sourceCommit)) {
        $versionFile = Join-Path (Split-Path -Parent $ServiceRoot) 'VERSION.txt'
        $sourceCommit = if (Test-Path $versionFile) {
            ([string](@(Get-Content $versionFile | Where-Object { $_ -like 'SourceCommit=*' } | Select-Object -First 1))).Substring('SourceCommit='.Length)
        } else { 'unknown' }
    }
    $sourceStatus = @(& git -C $ServiceRoot status --porcelain=v1 --untracked-files=normal 2>$null)
    $sourceDirty = if ($LASTEXITCODE -eq 0) {
        $sourceStatus.Count -gt 0
    } else {
        $versionFile = Join-Path (Split-Path -Parent $ServiceRoot) 'VERSION.txt'
        $dirtyLine = if (Test-Path $versionFile) { [string](@(Get-Content $versionFile | Where-Object { $_ -like 'SourceDirty=*' } | Select-Object -First 1)) } else { '' }
        if ($dirtyLine -eq 'SourceDirty=True') { $true } elseif ($dirtyLine -eq 'SourceDirty=False') { $false } else { $null }
    }
    $hostArtifactPath = if (Test-Path -LiteralPath (Join-Path $ServiceRoot 'MesIngest.Host.dll') -PathType Leaf) {
        Join-Path $ServiceRoot 'MesIngest.Host.dll'
    } else {
        Join-Path $ServiceRoot 'MesIngest.Host.exe'
    }
    $hostArtifact = Get-Item -LiteralPath $hostArtifactPath
    $hostSha256 = Get-FileSha256 $hostArtifact.FullName
    $hostFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostArtifact.FullName).FileVersion
    if ($null -ne $attestation) {
        $tier1.buildBound = [string]::Equals(
                [string]$attestation.sourceCommit,
                $sourceCommit,
                [StringComparison]::Ordinal) -and
            [string]::Equals(
                [string]$attestation.hostAssemblySha256,
                $hostSha256,
                [StringComparison]::OrdinalIgnoreCase)
    }
    $tier1.satisfied = $tier1.baseSatisfied -and $tier1.buildBound
    $tier1.diagnosticCode = if ($tier1.satisfied) {
        'SQL_TIER1_ATTESTATION_VERIFIED'
    } elseif ($tier1.baseSatisfied) {
        'SQL_TIER1_BUILD_MISMATCH'
    } else {
        [string]$tier1.diagnosticCode
    }
    $gateFailures = New-Object System.Collections.ArrayList
    @(Get-EvidenceGateFailures `
        -QueryEvidence @($queryEvidence) `
        -ActualPlanCount $planEvents.Count `
        -StatementMetricCount $statementEvents.Count `
        -RowCounts $rowCounts `
        -Tier1Satisfied ([bool]($tier1.satisfied -or $FastCapacityProjection -or $AcceleratedConcurrencyStability)) `
        -SourceCommit $sourceCommit `
        -CanonicalScaleProfile $canonicalScaleProfile `
        -QuerySurface $QuerySurface) | ForEach-Object { [void]$gateFailures.Add($_) }

    $growthComparison = [ordered]@{
        provided = $false
        baselineEvidencePath = $null
        baselineLogicalReads = $null
        observedLogicalReads = $null
        allowedLogicalReads = $null
        baselineMaxGrantedMemoryKb = $null
        observedMaxGrantedMemoryKb = $null
        surfaceComparisons = @()
        passed = $null
    }
    if (-not [string]::IsNullOrWhiteSpace($BaselineEvidencePath)) {
        $growthComparison.provided = $true
        $growthComparison.baselineEvidencePath = [IO.Path]::GetFullPath($BaselineEvidencePath)
        try {
            if (-not (Test-Path -LiteralPath $BaselineEvidencePath -PathType Leaf)) {
                throw 'Baseline evidence file does not exist.'
            }
            $baselineReport = Get-Content -Raw -LiteralPath $BaselineEvidencePath | ConvertFrom-Json
            $baselineSelectedSurfaces = @(if ($QuerySurface -eq 'All') {
                $baselineReport.queries | Where-Object {
                    $_.name -like 'DemandSeries*' -or
                    $boundedCurrentSurfaceFailurePrefixes.ContainsKey([string]$_.name)
                }
            } elseif ($QuerySurface -eq 'DemandSeries') {
                $baselineReport.queries | Where-Object { $_.name -like 'DemandSeries*' }
            } else {
                $baselineReport.queries | Where-Object { $_.name -eq $QuerySurface }
            })
            $observedSelectedSurfaces = @(if ($QuerySurface -eq 'All') {
                $queryEvidence | Where-Object {
                    $_.name -like 'DemandSeries*' -or
                    $boundedCurrentSurfaceFailurePrefixes.ContainsKey([string]$_.name)
                }
            } elseif ($QuerySurface -eq 'DemandSeries') {
                $queryEvidence | Where-Object { $_.name -like 'DemandSeries*' }
            } else {
                $queryEvidence | Where-Object { $_.name -eq $QuerySurface }
            })
            if ($baselineSelectedSurfaces.Count -eq 0 -or $observedSelectedSurfaces.Count -eq 0 -or
                -not [bool]$baselineReport.gate.passed -or
                [string]$baselineReport.profile.querySurface -ne $QuerySurface -or
                [string]$baselineReport.profile.evidenceScale -ne 'empty' -or
                [string]$baselineReport.sourceCommit -ne $sourceCommit -or
                [string]$baselineReport.build.hostSha256 -ne $hostSha256 -or
                [long]$baselineReport.profile.seriesCount -ne $SeriesCount -or
                [long]$baselineReport.profile.observationsPerRound -ne $ObservationsPerRound -or
                [long]$baselineReport.profile.roundIntervalSeconds -ne $RoundIntervalSeconds -or
                [long]$baselineReport.replay.warmupCount -ne $WarmupCount -or
                [long]$baselineReport.replay.measurementCount -ne $MeasurementCount -or
                [string]$baselineReport.sqlServer.dataSource -ne [string]$masterBuilder.DataSource -or
                [string]$baselineReport.sqlServer.productVersion -ne [string]$serverIdentity.product_version -or
                [long]$baselineReport.sqlServer.maxServerMemoryMb -ne [long]$serverIdentity.max_server_memory_mb -or
                [long]$baselineReport.contract.compatibilityLevel -ne [long]$databaseConfiguration.compatibilityLevel -or
                [string]$baselineReport.contract.recoveryModel -ne [string]$databaseConfiguration.recoveryModel) {
                throw "Baseline evidence is not a passing $QuerySurface run for this source commit."
            }
            $surfaceComparisons = New-Object System.Collections.ArrayList
            foreach ($observedSurface in $observedSelectedSurfaces) {
                $baselineSurface = @($baselineSelectedSurfaces | Where-Object { $_.name -eq $observedSurface.name }) |
                    Select-Object -First 1
                if ($null -eq $baselineSurface) { throw "Baseline is missing $($observedSurface.name)." }
                $logicalReadSlack = if ($historicalObjectSurfaceFailurePrefixes.ContainsKey($QuerySurface)) {
                    20L
                } else { 200L }
                $surfaceAllowed = [long][Math]::Max(
                    [Math]::Ceiling([long]$baselineSurface.logicalReads * 1.10),
                    [long]$baselineSurface.logicalReads + $logicalReadSlack)
                $rawObservationAllowed = [long][Math]::Max(
                    [Math]::Ceiling([long]$baselineSurface.rawObservationLogicalReads * 1.10),
                    [long]$baselineSurface.rawObservationLogicalReads + 20L)
                $surfaceLogicalReadsPassed = [long]$observedSurface.logicalReads -le $surfaceAllowed
                $rawObservationLogicalReadsPassed =
                    -not $historicalObjectSurfaceFailurePrefixes.ContainsKey($QuerySurface) -or
                    [long]$observedSurface.rawObservationLogicalReads -le $rawObservationAllowed
                [void]$surfaceComparisons.Add([pscustomobject][ordered]@{
                    name = [string]$observedSurface.name
                    baselineLogicalReads = [long]$baselineSurface.logicalReads
                    observedLogicalReads = [long]$observedSurface.logicalReads
                    allowedLogicalReads = $surfaceAllowed
                    baselineRawObservationLogicalReads = [long]$baselineSurface.rawObservationLogicalReads
                    observedRawObservationLogicalReads = [long]$observedSurface.rawObservationLogicalReads
                    allowedRawObservationLogicalReads = $rawObservationAllowed
                    passed = $surfaceLogicalReadsPassed -and $rawObservationLogicalReadsPassed
                })
            }
            $baselineLogicalReads = [long](($baselineSelectedSurfaces | Measure-Object -Property logicalReads -Sum).Sum)
            $observedLogicalReads = [long](($observedSelectedSurfaces | Measure-Object -Property logicalReads -Sum).Sum)
            $allowedLogicalReads = [long][Math]::Max(
                [Math]::Ceiling($baselineLogicalReads * 1.10),
                $baselineLogicalReads + 1000L)
            $growthComparison.baselineLogicalReads = $baselineLogicalReads
            $growthComparison.observedLogicalReads = $observedLogicalReads
            $growthComparison.allowedLogicalReads = $allowedLogicalReads
            $growthComparison.baselineMaxGrantedMemoryKb = [long](
                ($baselineSelectedSurfaces | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum)
            $growthComparison.observedMaxGrantedMemoryKb = [long](
                ($observedSelectedSurfaces | Measure-Object -Property maxGrantedMemoryKb -Maximum).Maximum)
            $growthComparison.surfaceComparisons = @($surfaceComparisons)
            $logicalReadGrowthPassed = $observedLogicalReads -le $allowedLogicalReads -and
                @($surfaceComparisons | Where-Object { -not [bool]$_.passed }).Count -eq 0
            $memoryGrantGrowthPassed = $growthComparison.observedMaxGrantedMemoryKb -le
                ($growthComparison.baselineMaxGrantedMemoryKb + 1024L)
            $growthComparison.passed = $logicalReadGrowthPassed -and $memoryGrantGrowthPassed
            $growthFailurePrefix = Get-BoundedQuerySurfaceFailurePrefix $QuerySurface
            if (-not $logicalReadGrowthPassed) {
                [void]$gateFailures.Add("$($growthFailurePrefix)_LOGICAL_READ_GROWTH")
            }
            if (-not $memoryGrantGrowthPassed) {
                [void]$gateFailures.Add("$($growthFailurePrefix)_MEMORY_GRANT_GROWTH")
            }
        } catch {
            $growthComparison.passed = $false
            $invalidBaselinePrefix = Get-BoundedQuerySurfaceFailurePrefix $QuerySurface
            [void]$gateFailures.Add("INVALID_$($invalidBaselinePrefix)_BASELINE")
        }
    }

    if ($AcceleratedConcurrencyStability) {
        $stabilityEvidence.behavior.currentLogicalReadGrowthPassed =
            [bool]($growthComparison.provided -and $growthComparison.passed)
        $stabilityResult = Get-AcceleratedStabilityResult $stabilityEvidence
        @($stabilityResult.failures) | ForEach-Object { [void]$gateFailures.Add($_) }
    }

    if ($FastCapacityProjection -and $historyRoundCount -gt 0) {
        try {
            $capacityBaseline = Get-Content -Raw -LiteralPath $BaselineEvidencePath | ConvertFrom-Json
            $requiredRawIndexes = @(
                'PK_MesIngest_DemandRawObservations',
                'IX_MesIngest_DemandRawObservations_Series',
                'IX_MesIngest_DemandRawObservations_Demand')
            $rawIndexAllocations = @($storage.allocations | Where-Object {
                $_.table_name -eq 'DemandRawObservations' -and
                $requiredRawIndexes -contains [string]$_.index_name
            })
            $pageCompressionVerified = $rawIndexAllocations.Count -eq $requiredRawIndexes.Count -and
                @($rawIndexAllocations | Where-Object { $_.data_compression_desc -ne 'PAGE' }).Count -eq 0
            $dataFiles = @($storage.physicalFiles | Where-Object { $_.type_desc -eq 'ROWS' })
            $autoGrowthVerified = $dataFiles.Count -gt 0 -and
                @($storage.physicalFiles | Where-Object {
                    [bool]$_.is_percent_growth -or [double]$_.growth_value -le 0
                }).Count -eq 0
            $dataFileGrowthMb = [double](($dataFiles | Measure-Object -Property growth_value -Maximum).Maximum)
            $peakLogUsedMb = [double](($capacityResourceSnapshots | Measure-Object -Property usedLogMb -Maximum).Maximum)
            $peakPhysicalLogMb = [double](($capacityResourceSnapshots | Measure-Object -Property totalLogMb -Maximum).Maximum)
            $lastResource = @($capacityResourceSnapshots)[@($capacityResourceSnapshots).Count - 1]
            $firstResource = @($capacityResourceSnapshots)[0]
            $afterQueryResource = @($capacityResourceSnapshots | Where-Object { $_.stage -eq 'after-query-evidence' }) | Select-Object -Last 1
            $databaseVersionStorePeakMb = [double](($capacityResourceSnapshots | Measure-Object -Property databaseVersionStoreMb -Maximum).Maximum)
            $tempdbVersionStorePeakMb = [double](($capacityResourceSnapshots | Measure-Object -Property tempdbVersionStoreMb -Maximum).Maximum)
            $tempdbUserObjectsImpactMb = [double](($capacityResourceSnapshots | Measure-Object -Property tempdbUserObjectsMb -Maximum).Maximum) -
                [double]$firstResource.tempdbUserObjectsMb
            $tempdbInternalObjectsImpactMb = [double](($capacityResourceSnapshots | Measure-Object -Property tempdbInternalObjectsMb -Maximum).Maximum) -
                [double]$firstResource.tempdbInternalObjectsMb
            $tombstoneAllocation = @($cleanupEvidence.storageAfterCleanup.allocations | Where-Object {
                $_.table_name -eq 'ArchivedDemandKeyTombstones'
            })
            $tombstoneLogicalUsedMb = [double](($tombstoneAllocation | Measure-Object -Property logical_used_mb -Sum).Sum)
            $capacityInput = [pscustomobject][ordered]@{
                baseline = $capacityBaseline
                sample = [pscustomobject][ordered]@{
                    rawObservationCount = [long]$rowCounts.raw_observations
                    historyObservationCount = $historyObservationCount
                    historyRoundCount = $historyRoundCount
                    roundIntervalSeconds = $RoundIntervalSeconds
                    observationsPerRound = $ObservationsPerRound
                    storage = $storage
                    checkpoints = @($capacityCheckpoints)
                    dataFileGrowthMb = $dataFileGrowthMb
                    peakLogUsedMb = $peakLogUsedMb
                    peakPhysicalLogMb = $peakPhysicalLogMb
                    tombstoneObservedCount = [long]$cleanupEvidence.tombstonesWritten
                    tombstoneLogicalUsedMb = $tombstoneLogicalUsedMb
                    projectedTombstoneCount = [long]$rowCounts.archived_series
                    databaseVersionStorePeakMb = $databaseVersionStorePeakMb
                    tempdbVersionStorePeakMb = $tempdbVersionStorePeakMb
                    tempdbUserObjectsImpactMb = $tempdbUserObjectsImpactMb
                    tempdbInternalObjectsImpactMb = $tempdbInternalObjectsImpactMb
                }
                environment = [pscustomobject][ordered]@{
                    recoveryModel = [string]$databaseConfiguration.recoveryModel
                    logReuseWait = [string]$lastResource.logReuseWait
                    compatibilityLevel = [int]$databaseConfiguration.compatibilityLevel
                    maxServerMemoryMb = [int]$serverIdentity.max_server_memory_mb
                    pageCompressionVerified = $pageCompressionVerified
                    autoGrowthVerified = $autoGrowthVerified
                    versionStoreMeasured = @($capacityResourceSnapshots).Count -ge 3
                    tempdbMeasured = @($capacityResourceSnapshots).Count -ge 3
                }
                cleanup = $cleanupEvidence
            }
            $capacityProjection = Get-FastCapacityProjection $capacityInput

            $allocationProjection = New-Object System.Collections.ArrayList
            foreach ($sampleAllocation in @($storage.allocations)) {
                $baselineAllocation = @($capacityBaseline.storage.allocations | Where-Object {
                    [string]$_.table_name -eq [string]$sampleAllocation.table_name -and
                    [string]$_.index_name -eq [string]$sampleAllocation.index_name -and
                    [string]$_.allocation_kind -eq [string]$sampleAllocation.allocation_kind
                }) | Select-Object -First 1
                $baselineAllocationMb = if ($null -eq $baselineAllocation) { 0.0 } else { [double]$baselineAllocation.logical_used_mb }
                $observedGrowthMb = [Math]::Max(0.0, [double]$sampleAllocation.logical_used_mb - $baselineAllocationMb)
                $projectedMb = ($baselineAllocationMb + (($observedGrowthMb / $historyRoundCount) * [long]$capacityProjection.model.targetRounds)) * 1.30
                [void]$allocationProjection.Add([pscustomobject][ordered]@{
                    table = [string]$sampleAllocation.table_name
                    index = [string]$sampleAllocation.index_name
                    kind = [string]$sampleAllocation.allocation_kind
                    compression = [string]$sampleAllocation.data_compression_desc
                    sampleRows = [long]$sampleAllocation.row_count
                    baselineLogicalUsedMb = $baselineAllocationMb
                    sampleLogicalUsedMb = [double]$sampleAllocation.logical_used_mb
                    observedGrowthMb = $observedGrowthMb
                    projectedThirtyDayWithMarginMb = $projectedMb
                })
            }
            $capacityProjection | Add-Member -NotePropertyName allocationProjection -NotePropertyValue @($allocationProjection)
            $capacityProjection | Add-Member -NotePropertyName resourceSnapshots -NotePropertyValue @($capacityResourceSnapshots)
            $capacityProjection | Add-Member -NotePropertyName sampleCheckpoints -NotePropertyValue @($capacityCheckpoints)
            $capacityProjection | Add-Member -NotePropertyName resourceImpact -NotePropertyValue ([pscustomobject][ordered]@{
                databaseVersionStorePeakMb = $databaseVersionStorePeakMb
                tempdbVersionStorePeakMb = $tempdbVersionStorePeakMb
                tempdbVersionStoreDeltaThroughQueryMb = [double]$afterQueryResource.tempdbVersionStoreMb - [double]$firstResource.tempdbVersionStoreMb
                tempdbUserObjectsDeltaThroughQueryMb = [double]$afterQueryResource.tempdbUserObjectsMb - [double]$firstResource.tempdbUserObjectsMb
                tempdbInternalObjectsDeltaThroughQueryMb = [double]$afterQueryResource.tempdbInternalObjectsMb - [double]$firstResource.tempdbInternalObjectsMb
                peakLogUsedMb = $peakLogUsedMb
                finalLogReuseWait = [string]$lastResource.logReuseWait
            })
            $capacityProjection | Add-Member -NotePropertyName cleanup -NotePropertyValue $cleanupEvidence
            $capacityProjection | Add-Member -NotePropertyName environment -NotePropertyValue $capacityInput.environment
            $capacityProjection | Add-Member -NotePropertyName ldfIncrementMb -NotePropertyValue `
                ($peakPhysicalLogMb - [double]$capacityBaseline.storage.ldfMb)
            $capacityProjection | Add-Member -NotePropertyName physicalDataIncrementMb -NotePropertyValue `
                ([double]$storage.physicalDataMb - [double]$capacityBaseline.storage.physicalDataMb)
            $capacityProjection | Add-Member -NotePropertyName tombstoneAllocations -NotePropertyValue @($tombstoneAllocation)
            $capacityProjection | Add-Member -NotePropertyName escalation -NotePropertyValue ([pscustomobject][ordered]@{
                required = [bool]$capacityProjection.escalationRequired
                releaseBlocked = [bool]$capacityProjection.escalationRequired
                reasonCodes = @($capacityProjection.failures)
                nextValidation = 'Invoke-ScaleAndQueryEvidence.ps1 -ProfileDays 30 -ConfirmFullScaleEscalation MESINGEST_FULL_SCALE_ESCALATION'
                contentAddressingDecision = 'Re-evaluate only if full-scale validation also fails.'
            })
            @($capacityProjection.failures) | ForEach-Object { [void]$gateFailures.Add($_) }
        } catch {
            [void]$gateFailures.Add('CAPACITY_MODEL_EVIDENCE_UNCERTAIN')
            $capacityProjection = [pscustomobject][ordered]@{
                passed = $false
                escalationRequired = $true
                failures = @('CAPACITY_MODEL_EVIDENCE_UNCERTAIN')
                errorType = $_.Exception.GetType().Name
                model = [pscustomobject][ordered]@{
                    targetDays = 30
                    targetRounds = [long][Math]::Floor((30.0 * 86400.0) / $RoundIntervalSeconds)
                    targetRawObservationRows = [long][Math]::Floor((30.0 * 86400.0) / $RoundIntervalSeconds) * [long]$ObservationsPerRound
                    safetyMarginFraction = 0.30
                    escalationFraction = 0.70
                }
                prediction = [pscustomobject][ordered]@{
                    logicalUsedMb = $null; physicalDataMb = $null; ldfMb = $null
                    tombstoneMb = $null; databaseVersionStorePeakMb = $null; tempdbImpactMb = $null
                }
                escalation = [pscustomobject][ordered]@{
                    required = $true; releaseBlocked = $true
                    reasonCodes = @('CAPACITY_MODEL_EVIDENCE_UNCERTAIN')
                    nextValidation = 'Resolve incomplete fast evidence before any full-scale run.'
                    contentAddressingDecision = 'Not evaluated from incomplete evidence.'
                }
            }
        }
    }

    $report = [ordered]@{
        schemaVersion = 1
        runId = $runId
        startedAt = $startedAt.ToString('o')
        completedAt = [DateTimeOffset]::UtcNow.ToString('o')
        sourceCommit = $sourceCommit
        build = [ordered]@{
            sourceCommit = $sourceCommit; sourceDirty = $sourceDirty; configuration = $BuildConfiguration
            hostFile = $hostArtifact.Name; hostFileVersion = $hostFileVersion; hostSha256 = $hostSha256
        }
        database = [ordered]@{ name = $DatabaseName; ownerRunId = $runId; removedAfterEvidence = -not $KeepDatabase }
        sqlServer = [ordered]@{
            dataSource = $masterBuilder.DataSource; productVersion = $serverIdentity.product_version
            productMajor = $serverIdentity.product_major; engineEdition = $serverIdentity.engine_edition
            maxServerMemoryMb = $serverIdentity.max_server_memory_mb
        }
        contract = [ordered]@{
            schemaVersion = $schemaIdentity.schemaVersion; contractVersion = $schemaIdentity.contractVersion
            historyEpoch = $schemaIdentity.historyEpoch
            compatibilityLevel = $databaseConfiguration.compatibilityLevel; recoveryModel = $databaseConfiguration.recoveryModel
        }
        profile = [ordered]@{
            historyDays = $profile.historyDays
            distribution = if ($canonicalScaleProfile) { $profile.distribution } else { 'diagnostic-override' }
            canonical = $canonicalScaleProfile; querySurface = $QuerySurface; seed = 8005
            fastCapacityProjection = [bool]$FastCapacityProjection
            acceleratedConcurrencyStability = [bool]$AcceleratedConcurrencyStability
            evidenceScale = if ($RepresentativeHistoryRounds -gt 0) { 'representative-history' } elseif ($ProfileDays -eq 0) { 'empty' } else { 'full-profile' }
            anchorUtc = $AnchorUtc.ToUniversalTime().ToString('o'); roundIntervalSeconds = $RoundIntervalSeconds
            observationsPerRound = $ObservationsPerRound; historyRoundCount = $historyRoundCount
            expectedRawObservationCount = $expectedTotalObservationCount; seriesCount = $SeriesCount
            activeRatio = $profile.activeRatio; archivedRatio = $profile.archivedRatio; errorRatio = $profile.errorRatio
        }
        data = $rowCounts
        replay = [ordered]@{
            warmupCount = $WarmupCount; measurementCount = $MeasurementCount
            queryParameters = @($surfaces | ForEach-Object { [ordered]@{ name = $_.name; path = $_.path } }) + $(
                if ($measureRawEvidence) { @([ordered]@{ name = 'RawEvidence'; path = '/raw-observations' }) } else { @() })
        }
        queries = @($queryEvidence)
        growthComparison = $growthComparison
        statementMetrics = @($statementMetrics)
        actualPlans = @($planSummaries)
        storage = $storage
        fastCapacityProjection = $capacityProjection
        acceleratedConcurrencyStability = if ($null -eq $stabilityEvidence) { $null } else {
            [ordered]@{ evidence = $stabilityEvidence; result = $stabilityResult }
        }
        tests = $tier1 + [ordered]@{
            deferredByFastCapacityProjection = [bool]($FastCapacityProjection -and -not $tier1.satisfied)
            deferredByAcceleratedConcurrencyStability = [bool]($AcceleratedConcurrencyStability -and -not $tier1.satisfied)
        }
        gate = [ordered]@{ passed = $gateFailures.Count -eq 0; failures = @($gateFailures) }
        release = if ($AcceleratedConcurrencyStability) {
            [ordered]@{
                ready = $false
                ticket28StabilityPassed = $gateFailures.Count -eq 0
                ticket27CapacityBlocked = $true
                ticket27CapacityBlockerPreserved = [bool]$capacityBlocker.preserved
                ticket27CapacityEvidence = $capacityBlocker
                statement = 'Ticket 28 stability cannot clear the independent Ticket 27 full-scale capacity blocker.'
            }
        } else { $null }
        safety = [ordered]@{ isolatedDatabaseOnly = $true; productionDatabaseRead = $false; credentialsWritten = $false; connectionStringWritten = $false }
    }
    $jsonPath = Join-Path $runDirectory 'scale-query-evidence.json'
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    $manifestPath = Join-Path $runDirectory 'replay-manifest.json'
    [ordered]@{
        schemaVersion = 1; runId = $runId; sourceCommit = $sourceCommit; build = $report.build; schema = $schemaIdentity.schemaVersion
        contract = $schemaIdentity.contractVersion; historyEpoch = $schemaIdentity.historyEpoch
        sqlProductVersion = $serverIdentity.product_version
        profile = $report.profile; replay = $report.replay
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $markdownPath = Join-Path $runDirectory 'scale-query-evidence.md'
    $markdownLines = @(
        '# MesIngest scale and query evidence'
        ''
        "- Run: $runId"
        "- Profile: $ProfileDays days; raw observations: $($rowCounts.raw_observations)"
        "- Contract/schema: $($schemaIdentity.contractVersion) / $($schemaIdentity.schemaVersion)"
        "- HistoryEpoch: $($schemaIdentity.historyEpoch)"
        "- SQL Server: $($serverIdentity.product_version)"
        "- Actual plans: $($planEvents.Count); statement metrics: $($statementEvents.Count)"
        "- SQL Tier 1: Failed=$($tier1.failed), Passed=$($tier1.passed), Skipped=$($tier1.sqlSkippedTests), Total=$($tier1.total)"
        "- Gate passed: $($report.gate.passed)"
        "- Failures: $(@($gateFailures) -join ', ')"
    )
    if ($null -ne $capacityProjection) {
        if ($null -ne $capacityProjection.prediction.logicalUsedMb) {
            $markdownLines += @(
                "- Fast capacity projected logical used: $([Math]::Round([double]$capacityProjection.prediction.logicalUsedMb, 3)) MB"
                "- Fast capacity projected physical data: $([Math]::Round([double]$capacityProjection.prediction.physicalDataMb, 3)) MB"
                "- Fast capacity projected LDF: $([Math]::Round([double]$capacityProjection.prediction.ldfMb, 3)) MB"
            )
        } else {
            $markdownLines += '- Fast capacity projections unavailable because model evidence was incomplete.'
        }
        $markdownLines += @(
            "- Fast capacity 30-day rows: $($capacityProjection.model.targetRawObservationRows); safety margin: 30%; escalation threshold: 70%"
            "- Fast capacity escalation required: $($capacityProjection.escalationRequired)"
        )
    }
    if ($null -ne $stabilityResult) {
        $markdownLines += @(
            "- Accelerated stability duration: $([Math]::Round([double]$stabilityEvidence.workload.durationSeconds / 60.0, 2)) minutes"
            "- Accelerated stability operations: polls=$($stabilityEvidence.workload.operations.totalPolls), Watch=$($stabilityEvidence.workload.operations.watchApiReads), frozen=$($stabilityEvidence.workload.operations.frozenDetailReads), catalog=$($stabilityEvidence.workload.operations.referenceCatalogReads), cleanup=$($stabilityEvidence.workload.operations.cleanupChecks)"
            "- Accelerated stability P95/P99: $([Math]::Round([double]$stabilityEvidence.latency.p95LatencyMs, 3)) / $([Math]::Round([double]$stabilityEvidence.latency.p99LatencyMs, 3)) ms"
            "- Accelerated stability soak escalation required: $($stabilityResult.soakEscalationRequired)"
            "- Release ready: False; Ticket 27 capacity blocker preserved: $([bool]$capacityBlocker.preserved)"
            "- Ticket 27 capacity projection remains: logical $($capacityBlocker.projectedLogicalUsedMb) MB; physical $($capacityBlocker.projectedPhysicalDataMb) MB; nonlinearity $($capacityBlocker.linearityRatio)"
        )
    }
    $markdownLines | Set-Content -LiteralPath $markdownPath -Encoding UTF8
    $inventoryPath = Join-Path $runDirectory 'sha256-inventory.json'
    @(Get-ChildItem -LiteralPath $runDirectory -File | Select-Object -ExpandProperty FullName) | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [pscustomobject][ordered]@{ file = $item.Name; length = $item.Length; sha256 = Get-FileSha256 $_ }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryPath -Encoding UTF8

    if ($AcceleratedConcurrencyStability) {
        Write-Output "MESINGEST_SCALE_EVIDENCE: ticket28Passed=$($report.gate.passed) releaseReady=False ticket27CapacityBlocked=True run=$runDirectory"
    } else {
        Write-Output "MESINGEST_SCALE_EVIDENCE: passed=$($report.gate.passed) run=$runDirectory"
    }
    Write-Output "JSON=$jsonPath"
    if (-not $report.gate.passed) { throw "Scale evidence gate failed: $(@($gateFailures) -join ', ')" }
}
finally {
    $cleanupFailure = $null
    Stop-EvidenceHost $hostRun
    if ($xeventStarted) {
        try { [void](Invoke-SqlNonQuery $masterConnectionString "ALTER EVENT SESSION [$($xeventSession.Replace(']', ']]'))] ON SERVER STATE = STOP;") } catch { }
    }
    try {
        $exists = @(Invoke-SqlTable $masterConnectionString 'SELECT name FROM sys.server_event_sessions WHERE name = @name;' @{ '@name' = $xeventSession })
        if ($exists.Count -eq 1) { [void](Invoke-SqlNonQuery $masterConnectionString "DROP EVENT SESSION [$($xeventSession.Replace(']', ']]'))] ON SERVER;") }
    } catch { }
    if (-not [string]::IsNullOrWhiteSpace($xelBase)) {
        try {
            $xelPattern = $xelBase.Substring(0, $xelBase.Length - 4) + '*.xel'
            $xelFiles = @(Invoke-SqlTable $masterConnectionString @"
SELECT DISTINCT file_name
FROM sys.fn_xe_file_target_read_file(@xelPattern, NULL, NULL, NULL);
"@ @{ '@xelPattern' = $xelPattern } 300)
            $expectedPrefix = [IO.Path]::GetFullPath($xelBase.Substring(0, $xelBase.Length - 4))
            foreach ($xelFile in $xelFiles) {
                $resolvedXel = [IO.Path]::GetFullPath([string]$xelFile.file_name)
                if (-not $resolvedXel.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals([IO.Path]::GetExtension($resolvedXel), '.xel', [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing unsafe XEvent cleanup target: $resolvedXel"
                }
                [void](Invoke-SqlNonQuery $masterConnectionString 'EXEC master.sys.xp_delete_files @filePath;' @{ '@filePath' = $resolvedXel })
            }
        } catch { $cleanupFailure = $_ }
    }
    if ($stabilityXEventStarted -and -not [string]::IsNullOrWhiteSpace($stabilityXEventSession)) {
        try {
            [void](Invoke-SqlNonQuery $masterConnectionString `
                "ALTER EVENT SESSION [$($stabilityXEventSession.Replace(']', ']]'))] ON SERVER STATE = STOP;")
        } catch { }
    }
    if (-not [string]::IsNullOrWhiteSpace($stabilityXEventSession)) {
        try {
            $exists = @(Invoke-SqlTable $masterConnectionString `
                'SELECT name FROM sys.server_event_sessions WHERE name = @name;' `
                @{ '@name' = $stabilityXEventSession })
            if ($exists.Count -eq 1) {
                [void](Invoke-SqlNonQuery $masterConnectionString `
                    "DROP EVENT SESSION [$($stabilityXEventSession.Replace(']', ']]'))] ON SERVER;")
            }
        } catch { }
    }
    if (-not [string]::IsNullOrWhiteSpace($stabilityXelBase)) {
        try {
            $stabilityXelPattern = $stabilityXelBase.Substring(0, $stabilityXelBase.Length - 4) + '*.xel'
            $stabilityXelFiles = @(Invoke-SqlTable $masterConnectionString @"
SELECT DISTINCT file_name
FROM sys.fn_xe_file_target_read_file(@xelPattern, NULL, NULL, NULL);
"@ @{ '@xelPattern' = $stabilityXelPattern } 300)
            $expectedStabilityPrefix = [IO.Path]::GetFullPath(
                $stabilityXelBase.Substring(0, $stabilityXelBase.Length - 4))
            foreach ($stabilityXelFile in $stabilityXelFiles) {
                $resolvedStabilityXel = [IO.Path]::GetFullPath([string]$stabilityXelFile.file_name)
                if (-not $resolvedStabilityXel.StartsWith(
                        $expectedStabilityPrefix,
                        [StringComparison]::OrdinalIgnoreCase) -or
                    -not [string]::Equals(
                        [IO.Path]::GetExtension($resolvedStabilityXel),
                        '.xel',
                        [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Refusing unsafe stability XEvent cleanup target: $resolvedStabilityXel"
                }
                [void](Invoke-SqlNonQuery $masterConnectionString `
                    'EXEC master.sys.xp_delete_files @filePath;' `
                    @{ '@filePath' = $resolvedStabilityXel })
            }
        } catch { if ($null -eq $cleanupFailure) { $cleanupFailure = $_ } }
    }
    if ($databaseCreated -and -not $KeepDatabase) {
        $owned = $false
        if ($databaseOwned) {
            try {
                $marker = @(Invoke-SqlTable $databaseConnectionString @"
SELECT CONVERT(nvarchar(128), value) AS owner_run_id
FROM sys.extended_properties WHERE class = 0 AND name = N'$ownerProperty';
"@) | Select-Object -First 1
                $owned = $null -ne $marker -and [string]$marker.owner_run_id -ceq $runId
            } catch { $owned = $false }
        }
        if (-not $owned) {
            throw "Refusing cleanup because database ownership cannot be proven for '$DatabaseName'."
        }
        [void](Invoke-SqlNonQuery $masterConnectionString @"
IF DB_ID(@databaseName) IS NOT NULL
BEGIN
    ALTER DATABASE [$DatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$DatabaseName];
END
"@ @{ '@databaseName' = $DatabaseName })
    }
    if ($null -ne $cleanupFailure) { throw $cleanupFailure }
}
