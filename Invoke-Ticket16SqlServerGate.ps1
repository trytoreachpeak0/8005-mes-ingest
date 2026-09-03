#Requires -Version 7.5
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ResultsDirectory = (Join-Path $PSScriptRoot '.artifacts\ticket16-tests'),
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 99)]
    [int] $ExpectedProductMajor,
    [Parameter(Mandatory = $true)]
    [ValidateRange(80, 999)]
    [int] $ExpectedCompatibilityLevel
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Keep this count explicit: update it whenever tests are intentionally added to or
# removed from ProjectionCommitAtomicityConcurrencyTests.
$expectedTestCount = 5
$reportMarkerPrefix = 'MESINGEST_TICKET16_REPORT_JSON:'
$requiredCheckpoints = @(
    'RoundEvidencePersisted',
    'ProtectionAndUnassignedPersisted',
    'DemandProjectionPersisted',
    'AbsenceAndArchivePersisted',
    'CatalogPersisted',
    'BeforeCommit'
)

function Get-CanonicalPropertyValue {
    param(
        [AllowNull()]
        [object] $InputObject,
        [string] $Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -ne $property -and $null -ne $property.Value) {
        return $property.Value
    }
    return $null
}

function ConvertTo-NullableInt {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value) {
        return $null
    }

    $parsed = 0
    if ([int]::TryParse(
            [string] $Value,
            [Globalization.NumberStyles]::Integer,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref] $parsed)) {
        return $parsed
    }
    return $null
}

function ConvertTo-NamedResults {
    param([AllowNull()][object] $Value)

    $results = New-Object 'System.Collections.Generic.List[object]'
    if ($null -eq $Value) {
        return @()
    }

    foreach ($item in @($Value)) {
        $name = Get-CanonicalPropertyValue -InputObject $item -Name 'name'
        $passed = Get-CanonicalPropertyValue -InputObject $item -Name 'passed'
        if ([string]::IsNullOrWhiteSpace([string] $name)) {
            throw "Ticket 16 named results must use a non-empty canonical 'name' property."
        }
        if ($passed -isnot [bool]) {
            throw "Ticket 16 named result '$name' must use a Boolean canonical 'passed' property."
        }
        $results.Add([pscustomobject][ordered]@{
            name = [string] $name
            passed = [bool] $passed
        })
    }
    return @($results.ToArray())
}

function ConvertTo-NormalizedMarker {
    param([object] $Marker)

    $productName = Get-CanonicalPropertyValue -InputObject $Marker -Name 'sqlProductName'
    $productVersion = Get-CanonicalPropertyValue -InputObject $Marker -Name 'productVersion'
    $productMajor = Get-CanonicalPropertyValue -InputObject $Marker -Name 'productMajor'
    $engineEdition = Get-CanonicalPropertyValue -InputObject $Marker -Name 'engineEdition'
    $compatibilityLevel = Get-CanonicalPropertyValue -InputObject $Marker -Name 'compatibilityLevel'
    $checkpoints = Get-CanonicalPropertyValue -InputObject $Marker -Name 'checkpoints'
    $writers = Get-CanonicalPropertyValue -InputObject $Marker -Name 'writerConcurrency'
    $readers = Get-CanonicalPropertyValue -InputObject $Marker -Name 'readerConcurrency'
    $retries = Get-CanonicalPropertyValue -InputObject $Marker -Name 'retries'
    $replays = Get-CanonicalPropertyValue -InputObject $Marker -Name 'replays'
    $conflicts = Get-CanonicalPropertyValue -InputObject $Marker -Name 'conflicts'
    $assertions = Get-CanonicalPropertyValue -InputObject $Marker -Name 'assertions'

    return [pscustomobject][ordered]@{
        productName = if ($null -eq $productName) { $null } else { [string] $productName }
        productVersion = if ($null -eq $productVersion) { $null } else { [string] $productVersion }
        productMajor = ConvertTo-NullableInt -Value $productMajor
        engineEdition = ConvertTo-NullableInt -Value $engineEdition
        compatibilityLevel = ConvertTo-NullableInt -Value $compatibilityLevel
        checkpoints = @(ConvertTo-NamedResults -Value $checkpoints)
        writerConcurrency = ConvertTo-NullableInt -Value $writers
        readerConcurrency = ConvertTo-NullableInt -Value $readers
        retries = ConvertTo-NullableInt -Value $retries
        replays = ConvertTo-NullableInt -Value $replays
        conflicts = ConvertTo-NullableInt -Value $conflicts
        assertions = @(ConvertTo-NamedResults -Value $assertions)
    }
}

function Merge-NormalizedMarkers {
    param([object[]] $Markers)

    $checkpoints = New-Object 'System.Collections.Generic.List[object]'
    $assertions = New-Object 'System.Collections.Generic.List[object]'
    $productName = $null
    $productVersion = $null
    $productMajor = $null
    $engineEdition = $null
    $compatibilityLevel = $null
    $writerConcurrency = $null
    $readerConcurrency = $null
    $retries = $null
    $replays = $null
    $conflicts = $null

    foreach ($marker in $Markers) {
        if (-not [string]::IsNullOrWhiteSpace([string] $marker.productName)) {
            $productName = $marker.productName
        }
        if (-not [string]::IsNullOrWhiteSpace([string] $marker.productVersion)) {
            $productVersion = $marker.productVersion
        }
        foreach ($name in @('productMajor', 'engineEdition', 'compatibilityLevel')) {
            if ($null -ne $marker.$name) {
                $current = Get-Variable -Name $name -ValueOnly
                if ($null -ne $current -and [int] $current -ne [int] $marker.$name) {
                    throw "Conflicting '$name' values were reported by Ticket 16 markers."
                }
                Set-Variable -Name $name -Value $marker.$name
            }
        }
        foreach ($name in @('writerConcurrency', 'readerConcurrency', 'retries', 'replays', 'conflicts')) {
            if ($null -ne $marker.$name) {
                $current = Get-Variable -Name $name -ValueOnly
                if ($null -eq $current -or [int] $marker.$name -gt [int] $current) {
                    Set-Variable -Name $name -Value $marker.$name
                }
            }
        }
        foreach ($checkpoint in $marker.checkpoints) {
            $checkpoints.Add($checkpoint)
        }
        foreach ($assertion in $marker.assertions) {
            $assertions.Add($assertion)
        }
    }

    $mergedCheckpoints = @(
        $checkpoints |
            Group-Object -Property name |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    name = [string] $_.Name
                    passed = (@($_.Group | Where-Object { -not $_.passed }).Count -eq 0)
                }
            } |
            Sort-Object -Property name
    )
    $mergedAssertions = @(
        $assertions |
            Group-Object -Property name |
            ForEach-Object {
                [pscustomobject][ordered]@{
                    name = [string] $_.Name
                    passed = (@($_.Group | Where-Object { -not $_.passed }).Count -eq 0)
                }
            } |
            Sort-Object -Property name
    )

    return [pscustomobject][ordered]@{
        productName = $productName
        productVersion = $productVersion
        productMajor = $productMajor
        engineEdition = $engineEdition
        compatibilityLevel = $compatibilityLevel
        checkpoints = $mergedCheckpoints
        writerConcurrency = $writerConcurrency
        readerConcurrency = $readerConcurrency
        retries = $retries
        replays = $replays
        conflicts = $conflicts
        assertions = $mergedAssertions
    }
}

function ConvertTo-MarkdownCell {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string] $Value)) {
        return '(missing)'
    }
    return ([string] $Value).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}

$connectionString = [Environment]::GetEnvironmentVariable('MES_INGEST_TICKET01_SQLSERVER')
if ([string]::IsNullOrWhiteSpace($connectionString)) {
    throw 'Set MES_INGEST_TICKET01_SQLSERVER to an explicitly approved real SQL Server instance.'
}
if ($connectionString.IndexOf('localdb', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'Ticket 16 requires a real SQL Server; LocalDB cannot satisfy this gate.'
}

$testProject = Join-Path $PSScriptRoot 'MesIngest.Tests\MesIngest.Tests.csproj'
$resultsPath = [IO.Path]::GetFullPath($ResultsDirectory)
[IO.Directory]::CreateDirectory($resultsPath) | Out-Null
$trxName = 'ticket16-projection-commit-atomicity-concurrency.trx'
$trxPath = Join-Path $resultsPath $trxName
$jsonReportPath = Join-Path $resultsPath 'ticket16-gate-report.json'
$markdownReportPath = Join-Path $resultsPath 'ticket16-gate-report.md'
Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $jsonReportPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $markdownReportPath -Force -ErrorAction SilentlyContinue

$productMajorVariable = 'MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR'
$compatibilityVariable = 'MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL'
$oldProductMajor = [Environment]::GetEnvironmentVariable(
    $productMajorVariable,
    [EnvironmentVariableTarget]::Process)
$oldCompatibility = [Environment]::GetEnvironmentVariable(
    $compatibilityVariable,
    [EnvironmentVariableTarget]::Process)

try {
    [Environment]::SetEnvironmentVariable(
        $productMajorVariable,
        $ExpectedProductMajor.ToString([Globalization.CultureInfo]::InvariantCulture),
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $compatibilityVariable,
        $ExpectedCompatibilityLevel.ToString([Globalization.CultureInfo]::InvariantCulture),
        [EnvironmentVariableTarget]::Process)

    & dotnet test $testProject `
        --configuration $Configuration `
        --filter 'FullyQualifiedName~ProjectionCommitAtomicityConcurrencyTests' `
        --results-directory $resultsPath `
        --logger "trx;LogFileName=$trxName"
    $dotnetExitCode = $LASTEXITCODE

    $validationErrors = New-Object 'System.Collections.Generic.List[string]'
    if ($dotnetExitCode -ne 0) {
        $validationErrors.Add("Ticket 16 SQL Server tests exited with code $dotnetExitCode.")
    }

    $total = 0
    $executed = 0
    $passed = 0
    $failed = 0
    $notExecuted = 0
    $markerParseErrors = New-Object 'System.Collections.Generic.List[string]'
    $normalizedMarkers = New-Object 'System.Collections.Generic.List[object]'

    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        $validationErrors.Add("VSTest did not produce the required TRX: $trxPath")
    }
    else {
        [xml] $trx = Get-Content -Raw -LiteralPath $trxPath
        $counters = $trx.TestRun.ResultSummary.Counters
        if ($null -eq $counters) {
            $validationErrors.Add('The TRX has no ResultSummary/Counters element.')
        }
        else {
            $total = [int] $counters.total
            $executed = [int] $counters.executed
            $passed = [int] $counters.passed
            $failed = [int] $counters.failed
            $notExecuted = [int] $counters.notExecuted
        }

        foreach ($stdoutNode in $trx.SelectNodes("//*[local-name()='StdOut']")) {
            foreach ($line in ([string] $stdoutNode.InnerText -split "`r?`n")) {
                $prefixIndex = $line.IndexOf($reportMarkerPrefix, [StringComparison]::Ordinal)
                if ($prefixIndex -lt 0) {
                    continue
                }
                $json = $line.Substring($prefixIndex + $reportMarkerPrefix.Length).Trim()
                try {
                    $rawMarker = $json | ConvertFrom-Json -DateKind String
                    $normalizedMarkers.Add((ConvertTo-NormalizedMarker -Marker $rawMarker))
                }
                catch {
                    $markerParseErrors.Add($_.Exception.Message)
                }
            }
        }
    }

    if ($total -ne $expectedTestCount -or
        $executed -ne $expectedTestCount -or
        $passed -ne $expectedTestCount -or
        $failed -ne 0 -or
        $notExecuted -ne 0) {
        $validationErrors.Add((
            'Ticket 16 gate requires exactly {0} passed and 0 skipped; observed total={1} ' +
            'executed={2} passed={3} failed={4} notExecuted={5}.' -f
            $expectedTestCount, $total, $executed, $passed, $failed, $notExecuted))
    }
    foreach ($parseError in $markerParseErrors) {
        $validationErrors.Add("Invalid $reportMarkerPrefix marker: $parseError")
    }
    if ($normalizedMarkers.Count -eq 0) {
        $validationErrors.Add(
            "The TRX StdOut contains no valid $reportMarkerPrefix aggregate or partial marker.")
        $evidence = Merge-NormalizedMarkers -Markers @()
    }
    else {
        $evidence = Merge-NormalizedMarkers -Markers $normalizedMarkers.ToArray()
    }

    if ($null -eq $evidence.productMajor -and
        -not [string]::IsNullOrWhiteSpace([string] $evidence.productVersion)) {
        $version = $null
        if ([Version]::TryParse([string] $evidence.productVersion, [ref] $version)) {
            $evidence.productMajor = $version.Major
        }
    }

    if ([string]::IsNullOrWhiteSpace([string] $evidence.productName)) {
        $validationErrors.Add('The report marker did not record the actual SQL product name/edition.')
    }
    if ([string]::IsNullOrWhiteSpace([string] $evidence.productVersion)) {
        $validationErrors.Add('The report marker did not record the actual SQL product version.')
    }
    if ($null -eq $evidence.productMajor -or $evidence.productMajor -ne $ExpectedProductMajor) {
        $validationErrors.Add((
            'Expected SQL product major {0}; marker reported {1}.' -f
            $ExpectedProductMajor, (ConvertTo-MarkdownCell $evidence.productMajor)))
    }
    if ($null -eq $evidence.engineEdition) {
        $validationErrors.Add('The report marker did not record the actual SQL engine edition.')
    }
    if ($null -eq $evidence.compatibilityLevel -or
        $evidence.compatibilityLevel -ne $ExpectedCompatibilityLevel) {
        $validationErrors.Add((
            'Expected database compatibility level {0}; marker reported {1}.' -f
            $ExpectedCompatibilityLevel, (ConvertTo-MarkdownCell $evidence.compatibilityLevel)))
    }

    $checkpointByName = @{}
    foreach ($checkpoint in $evidence.checkpoints) {
        $checkpointByName[$checkpoint.name] = $checkpoint
    }
    foreach ($checkpointName in $requiredCheckpoints) {
        if (-not $checkpointByName.ContainsKey($checkpointName)) {
            $validationErrors.Add("Missing fault-injection checkpoint '$checkpointName'.")
        }
        elseif (-not $checkpointByName[$checkpointName].passed) {
            $validationErrors.Add("Fault-injection checkpoint '$checkpointName' did not pass.")
        }
    }
    $unexpectedCheckpoints = @(
        $evidence.checkpoints | Where-Object { $_.name -notin $requiredCheckpoints }
    )
    if ($unexpectedCheckpoints.Count -gt 0 -or $checkpointByName.Count -ne $requiredCheckpoints.Count) {
        $validationErrors.Add(
            'The marker must report exactly the six named production fault-injection checkpoints.')
    }

    if ($null -eq $evidence.writerConcurrency -or $evidence.writerConcurrency -lt 2) {
        $validationErrors.Add('Writer concurrency must record at least 2 concurrent writers.')
    }
    if ($null -eq $evidence.readerConcurrency -or $evidence.readerConcurrency -lt 2) {
        $validationErrors.Add('Reader concurrency must record at least 2 concurrent readers.')
    }
    foreach ($counterName in @('retries', 'replays', 'conflicts')) {
        if ($null -eq $evidence.$counterName -or $evidence.$counterName -lt 1) {
            $validationErrors.Add("The marker must record at least one $counterName assertion.")
        }
    }
    if ($evidence.assertions.Count -eq 0) {
        $validationErrors.Add('The marker did not record named retry/replay/conflict assertions.')
    }
    foreach ($assertionKind in @('retry', 'replay', 'conflict')) {
        $matchingAssertions = @(
            $evidence.assertions |
                Where-Object { $_.name -match $assertionKind }
        )
        if ($matchingAssertions.Count -eq 0) {
            $validationErrors.Add(
                "The marker did not record a named $assertionKind assertion.")
        }
    }
    foreach ($assertion in $evidence.assertions) {
        if (-not $assertion.passed) {
            $validationErrors.Add("Reported assertion '$($assertion.name)' did not pass.")
        }
    }

    $checkpointReport = @(
        foreach ($checkpointName in $requiredCheckpoints) {
            [pscustomobject][ordered]@{
                name = $checkpointName
                passed = ($checkpointByName.ContainsKey($checkpointName) -and
                    [bool] $checkpointByName[$checkpointName].passed)
            }
        }
    )
    $status = if ($validationErrors.Count -eq 0) { 'PASSED' } else { 'FAILED' }
    $report = [pscustomobject][ordered]@{
        schemaVersion = 1
        gate = 'Ticket16ProjectionCommitAtomicityConcurrency'
        status = $status
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        configuration = $Configuration
        expected = [pscustomobject][ordered]@{
            sqlProductMajor = $ExpectedProductMajor
            compatibilityLevel = $ExpectedCompatibilityLevel
            testCount = $expectedTestCount
        }
        sqlServer = [pscustomobject][ordered]@{
            productName = $evidence.productName
            productVersion = $evidence.productVersion
            productMajor = $evidence.productMajor
            engineEdition = $evidence.engineEdition
            compatibilityLevel = $evidence.compatibilityLevel
            localDb = $false
        }
        faultInjectionCheckpoints = $checkpointReport
        concurrency = [pscustomobject][ordered]@{
            writers = $evidence.writerConcurrency
            readers = $evidence.readerConcurrency
        }
        idempotencyAndConflict = [pscustomobject][ordered]@{
            retries = $evidence.retries
            replays = $evidence.replays
            conflicts = $evidence.conflicts
            assertions = @($evidence.assertions)
        }
        tests = [pscustomobject][ordered]@{
            filter = 'FullyQualifiedName~ProjectionCommitAtomicityConcurrencyTests'
            total = $total
            executed = $executed
            passed = $passed
            failed = $failed
            skipped = $notExecuted
        }
        markersParsed = $normalizedMarkers.Count
        artifacts = [pscustomobject][ordered]@{
            trx = $trxPath
            jsonReport = $jsonReportPath
            markdownReport = $markdownReportPath
        }
        validationErrors = @($validationErrors.ToArray())
    }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonReportPath -Encoding UTF8

    $markdown = New-Object 'System.Collections.Generic.List[string]'
    $markdown.Add('# Ticket 16 SQL Server gate report')
    $markdown.Add('')
    $markdown.Add("- Status: **$status**")
    $markdown.Add(('- Generated (UTC): `{0}`' -f (ConvertTo-MarkdownCell $report.generatedAtUtc)))
    $markdown.Add(('- Configuration: `{0}`' -f (ConvertTo-MarkdownCell $Configuration)))
    $markdown.Add('')
    $markdown.Add('## SQL Server evidence')
    $markdown.Add('')
    $markdown.Add('| Field | Actual | Expected |')
    $markdown.Add('| --- | --- | --- |')
    $markdown.Add("| Product | $(ConvertTo-MarkdownCell $evidence.productName) | real SQL Server (LocalDB rejected) |")
    $markdown.Add("| Product version | $(ConvertTo-MarkdownCell $evidence.productVersion) | product major $ExpectedProductMajor |")
    $markdown.Add("| Product major | $(ConvertTo-MarkdownCell $evidence.productMajor) | $ExpectedProductMajor |")
    $markdown.Add("| Engine edition | $(ConvertTo-MarkdownCell $evidence.engineEdition) | recorded |")
    $markdown.Add("| Database compatibility | $(ConvertTo-MarkdownCell $evidence.compatibilityLevel) | $ExpectedCompatibilityLevel |")
    $markdown.Add('')
    $markdown.Add('## Fault-injection checkpoints')
    $markdown.Add('')
    $markdown.Add('| Checkpoint | Passed |')
    $markdown.Add('| --- | --- |')
    foreach ($checkpoint in $checkpointReport) {
        $markdown.Add("| $($checkpoint.name) | $($checkpoint.passed) |")
    }
    $markdown.Add('')
    $markdown.Add('## Concurrency and idempotency')
    $markdown.Add('')
    $markdown.Add('| Evidence | Observed | Gate minimum |')
    $markdown.Add('| --- | ---: | ---: |')
    $markdown.Add("| Concurrent writers | $(ConvertTo-MarkdownCell $evidence.writerConcurrency) | 2 |")
    $markdown.Add("| Concurrent readers | $(ConvertTo-MarkdownCell $evidence.readerConcurrency) | 2 |")
    $markdown.Add("| Retries | $(ConvertTo-MarkdownCell $evidence.retries) | 1 |")
    $markdown.Add("| Idempotent replays | $(ConvertTo-MarkdownCell $evidence.replays) | 1 |")
    $markdown.Add("| Content conflicts | $(ConvertTo-MarkdownCell $evidence.conflicts) | 1 |")
    $markdown.Add('')
    $markdown.Add('### Named assertions')
    $markdown.Add('')
    $markdown.Add('| Assertion | Passed |')
    $markdown.Add('| --- | --- |')
    foreach ($assertion in $evidence.assertions) {
        $markdown.Add("| $(ConvertTo-MarkdownCell $assertion.name) | $($assertion.passed) |")
    }
    $markdown.Add('')
    $markdown.Add('## VSTest results')
    $markdown.Add('')
    $markdown.Add('| Expected | Total | Executed | Passed | Failed | Skipped |')
    $markdown.Add('| ---: | ---: | ---: | ---: | ---: | ---: |')
    $markdown.Add("| $expectedTestCount | $total | $executed | $passed | $failed | $notExecuted |")
    $markdown.Add('')
    $markdown.Add('- Filter: `FullyQualifiedName~ProjectionCommitAtomicityConcurrencyTests`')
    $markdown.Add(('- TRX: `{0}`' -f (ConvertTo-MarkdownCell $trxPath)))
    $markdown.Add(('- JSON report: `{0}`' -f (ConvertTo-MarkdownCell $jsonReportPath)))
    if ($validationErrors.Count -gt 0) {
        $markdown.Add('')
        $markdown.Add('## Validation errors')
        $markdown.Add('')
        foreach ($validationError in $validationErrors) {
            $markdown.Add("- $(ConvertTo-MarkdownCell $validationError)")
        }
    }
    $markdown | Set-Content -LiteralPath $markdownReportPath -Encoding UTF8

    if ($validationErrors.Count -gt 0) {
        throw "Ticket 16 SQL Server gate failed. See $markdownReportPath"
    }

    Write-Output (((
        'MESINGEST_TICKET16_SQLSERVER_GATE_PASSED: passed={0} skipped={1} ' +
        'productVersion={2} engineEdition={3} compatibilityLevel={4} ' +
        'writers={5} readers={6} trx={7} json={8} markdown={9}') -f
        $passed, $notExecuted, $evidence.productVersion, $evidence.engineEdition,
        $evidence.compatibilityLevel, $evidence.writerConcurrency,
        $evidence.readerConcurrency, $trxPath, $jsonReportPath, $markdownReportPath))
}
finally {
    [Environment]::SetEnvironmentVariable(
        $productMajorVariable,
        $oldProductMajor,
        [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable(
        $compatibilityVariable,
        $oldCompatibility,
        [EnvironmentVariableTarget]::Process)
}
