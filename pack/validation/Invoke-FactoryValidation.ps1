#Requires -Version 5.1
<#
.SYNOPSIS
  Collect read-only MesIngest factory validation evidence for logical site A, B, or C.

.DESCRIPTION
  Issues GET requests only. It never connects directly to Oracle or SQL Server and never changes
  the approved MES query. The SharedSecret is read from an environment variable and is not written
  to the evidence directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("A", "B", "C")]
    [string] $LogicalSite,

    [string] $BaseUrl = "http://127.0.0.1:5088",

    [string] $OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "validation-runs"),

    [ValidateRange(1, 300)]
    [int] $RequestTimeoutSeconds = 30,

    [ValidateRange(1, 20)]
    [int] $PollSampleCount = 3,

    [ValidateRange(0, 300)]
    [int] $PollSampleIntervalSeconds = 12,

    [ValidateRange(1, 1800)]
    [int] $PollSampleWaitTimeoutSeconds = 120,

    [string] $SharedSecretEnvironmentVariable = "MES_INGEST_SHARED_SECRET",

    [ValidateRange(0, 300)]
    [int] $WatchObservationSeconds = 20,

    [string] $WatchLogDirectory = (Join-Path $env:LOCALAPPDATA "MesIngest.Watch\logs"),

    [string] $HostEventComputerName = "",

    [string] $ThinProbeLog = "",

    [string] $ThickProbeLog = ""
)

$ErrorActionPreference = "Stop"
$startedAt = [DateTimeOffset]::UtcNow
$runId = "run-{0}-{1}-{2}" -f $startedAt.ToString("yyyyMMddTHHmmssZ"), $LogicalSite.ToLowerInvariant(), ([Guid]::NewGuid().ToString("N").Substring(0, 8))
$runDirectory = Join-Path $OutputRoot $runId
if (Test-Path -LiteralPath $runDirectory) {
    throw "Refusing to overwrite validation run: $runDirectory"
}

New-Item -ItemType Directory -Path $runDirectory | Out-Null
New-Item -ItemType Directory -Path (Join-Path $runDirectory "api") | Out-Null
$metrics = New-Object System.Collections.ArrayList
$requestSequence = 0
$sharedSecret = [Environment]::GetEnvironmentVariable($SharedSecretEnvironmentVariable)
$canonicalQueryId = "MES_TASK_UNION"
$canonicalQuerySha256 = "54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae"
$canonicalQueryVersion = "$canonicalQueryId/sha256:$canonicalQuerySha256"

function Protect-ValidationText {
    param([AllowEmptyString()][string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $clean = $Text -replace '(?i)(Authorization\s*:\s*Bearer\s+)\S+', '$1[redacted]'
    $clean = $clean -replace '(?i)((?:SharedSecret|Password|Pwd)\s*[:=]\s*)[^;\s]+', '$1[redacted]'
    $clean = $clean -replace '(?i)((?:Data Source|Server|Initial Catalog|Database|User ID|UID|DSN)\s*=\s*)[^;\s]+', '$1[redacted]'
    return $clean
}

function Import-OracleProbeEvidence {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("Thin", "Thick")][string] $ExpectedMode,
        [AllowEmptyString()][string] $SourcePath,
        [Parameter(Mandatory = $true)][string] $OutputFile
    )

    $notExecuted = [ordered]@{
        attempted = $false
        connection_attempted = $false
        execution_scope = "NOT_EXECUTED"
        requested_mode = $ExpectedMode
        actual_mode = "NOT_AVAILABLE"
        driver = "NOT_AVAILABLE"
        query_id = $canonicalQueryId
        query_version = "NOT_AVAILABLE"
        query_sha256 = "NOT_AVAILABLE"
        outcome = "NOT_EXECUTED"
        row_count = 0
        duration_ms = 0
        result = "NOT_EXECUTED"
        log = $OutputFile
    }
    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        return [PSCustomObject]$notExecuted
    }

    $resolved = Resolve-Path -LiteralPath $SourcePath -ErrorAction Stop
    $safeText = Protect-ValidationText ([IO.File]::ReadAllText($resolved.Path))
    Set-Content -LiteralPath (Join-Path $runDirectory $OutputFile) -Value $safeText -Encoding UTF8

    $fields = @{}
    foreach ($line in ($safeText -split "`r?`n")) {
        if ($line -match '^([a-z_]+)=(.*)$') {
            $fields[$Matches[1].ToLowerInvariant()] = $Matches[2].Trim()
        }
    }

    $scope = if ($fields.ContainsKey("execution_scope")) { [string]$fields["execution_scope"] } else { "NOT_EXECUTED" }
    $connectionAttempted = $fields.ContainsKey("connection_attempted") -and
        [string]::Equals([string]$fields["connection_attempted"], "true", [StringComparison]::OrdinalIgnoreCase)
    $requestedMode = if ($fields.ContainsKey("requested_mode")) { [string]$fields["requested_mode"] } else { $ExpectedMode }
    $result = if ($fields.ContainsKey("result")) { ([string]$fields["result"]).ToUpperInvariant() } else { "NOT_EXECUTED" }

    # Offline, simulated, legacy, and malformed logs cannot be recorded as PASSED.
    if ($result -eq "PASSED" -and ($scope -ne "LIVE_ORACLE" -or -not $connectionAttempted)) {
        $result = "NOT_EXECUTED"
    }
    $actualMode = if ($fields.ContainsKey("actual_mode")) { [string]$fields["actual_mode"] } else { "NOT_AVAILABLE" }
    if ($result -eq "PASSED" -and
        -not [string]::Equals($requestedMode, $actualMode, [StringComparison]::OrdinalIgnoreCase)) {
        $result = "FAILED"
    }
    if ($result -notin @("PASSED", "FAILED", "NOT_EXECUTED")) {
        $result = "FAILED"
    }
    if (-not [string]::Equals($requestedMode, $ExpectedMode, [StringComparison]::OrdinalIgnoreCase)) {
        $result = "FAILED"
    }
    $queryId = if ($fields.ContainsKey("query_id")) { [string]$fields["query_id"] } else { "NOT_AVAILABLE" }
    $queryVersion = if ($fields.ContainsKey("query_version")) { [string]$fields["query_version"] } else { "NOT_AVAILABLE" }
    $querySha256 = if ($fields.ContainsKey("query_sha256")) { [string]$fields["query_sha256"] } else { "NOT_AVAILABLE" }
    $outcome = if ($fields.ContainsKey("outcome")) { [string]$fields["outcome"] } else { "NOT_EXECUTED" }
    if ($result -eq "PASSED" -and
        (-not [string]::Equals($outcome, "Success", [StringComparison]::OrdinalIgnoreCase) -or
         -not [string]::Equals($queryId, $canonicalQueryId, [StringComparison]::Ordinal) -or
         -not [string]::Equals($queryVersion, $canonicalQueryVersion, [StringComparison]::Ordinal) -or
         -not [string]::Equals($querySha256, $canonicalQuerySha256, [StringComparison]::Ordinal))) {
        $result = "FAILED"
    }

    return [PSCustomObject][ordered]@{
        attempted = $true
        connection_attempted = $connectionAttempted
        execution_scope = $scope
        requested_mode = $requestedMode
        actual_mode = $actualMode
        driver = if ($fields.ContainsKey("driver")) { [string]$fields["driver"] } else { "NOT_AVAILABLE" }
        query_id = $queryId
        query_version = $queryVersion
        query_sha256 = $querySha256
        outcome = $outcome
        row_count = if ($fields.ContainsKey("row_count")) { [long]$fields["row_count"] } else { 0 }
        duration_ms = if ($fields.ContainsKey("duration_ms")) { [long]$fields["duration_ms"] } else { 0 }
        result = $result
        log = $OutputFile
    }
}

function Get-ResponseBodyFromError {
    param($ErrorRecord)

    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -eq $response) { return "" }
        if ($null -ne $response.Content) {
            return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        $stream = $response.GetResponseStream()
        if ($null -eq $stream) { return "" }
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } catch {
        return ""
    }
}

function Invoke-ValidationGet {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $OutputFile,
        [int[]] $AllowedStatusCodes = @(200)
    )

    $script:requestSequence++
    $correlationId = "factory-{0}-{1:D4}" -f $runId, $script:requestSequence
    $headers = @{ "X-Correlation-Id" = $correlationId }
    if (-not [string]::IsNullOrWhiteSpace($sharedSecret)) {
        $headers["Authorization"] = "Bearer $sharedSecret"
    }

    $uri = [Uri]::new(([Uri]::new($BaseUrl.TrimEnd('/') + '/')), $RelativePath.TrimStart('/'))
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $statusCode = 0
    $body = ""
    $responseCorrelationId = $null
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -Headers $headers -TimeoutSec $RequestTimeoutSeconds -UseBasicParsing
        $statusCode = [int]$response.StatusCode
        $body = [string]$response.Content
        $responseCorrelationId = [string]$response.Headers["X-Correlation-Id"]
    } catch {
        if ($null -ne $_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
            $responseCorrelationId = [string]$_.Exception.Response.Headers["X-Correlation-Id"]
            $body = Get-ResponseBodyFromError $_
        } else {
            throw "GET $($uri.AbsolutePath) failed before an HTTP response: $(Protect-ValidationText $_.Exception.Message)"
        }
    } finally {
        $stopwatch.Stop()
    }

    $body = Protect-ValidationText $body
    $json = $null
    if (-not [string]::IsNullOrWhiteSpace($body)) {
        try { $json = $body | ConvertFrom-Json } catch { $json = $null }
    }

    $rowCount = $null
    $hasMore = $null
    if ($null -ne $json -and $null -ne $json.items) {
        $rowCount = @($json.items).Count
        $hasMore = [bool]$json.hasMore
    } elseif ($null -ne $json) {
        $rowCount = 1
    }

    $outputPath = Join-Path $runDirectory $OutputFile
    $parent = Split-Path -Parent $outputPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    Set-Content -LiteralPath $outputPath -Value $body -Encoding UTF8

    $metric = [ordered]@{
        captured_at = [DateTimeOffset]::UtcNow.ToString("o")
        logical_site = $LogicalSite
        label = $Label
        method = "GET"
        endpoint = $uri.AbsolutePath
        correlation_id = $correlationId
        response_correlation_id = $responseCorrelationId
        elapsed_ms = $stopwatch.ElapsedMilliseconds
        status_code = $statusCode
        row_count = $rowCount
        bytes = [Text.Encoding]::UTF8.GetByteCount($body)
        has_more = $hasMore
        timeout_seconds = $RequestTimeoutSeconds
    }
    [void]$metrics.Add([PSCustomObject]$metric)

    if ($AllowedStatusCodes -notcontains $statusCode) {
        throw "GET $($uri.AbsolutePath) returned HTTP $statusCode; evidence kept at $OutputFile"
    }

    return [PSCustomObject]@{ Json = $json; Body = $body; StatusCode = $statusCode; Metric = [PSCustomObject]$metric }
}

function Invoke-PagedGet {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $InitialRelativePath,
        [Parameter(Mandatory = $true)][string] $FileStem
    )

    $items = New-Object System.Collections.ArrayList
    $relativePath = $InitialRelativePath
    $page = 1
    do {
        if ($page -gt 10000) { throw "Pagination safety limit exceeded for $Label" }
        $file = "api/{0}-page-{1:D3}.json" -f $FileStem, $page
        $result = Invoke-ValidationGet -Label ("{0}-page-{1:D3}" -f $Label, $page) -RelativePath $relativePath -OutputFile $file
        if ($null -eq $result.Json -or $null -eq $result.Json.items) {
            throw "$Label did not return the required items/nextCursor/hasMore envelope"
        }
        foreach ($item in @($result.Json.items)) { [void]$items.Add($item) }

        $hasMore = [bool]$result.Json.hasMore
        $cursor = [string]$result.Json.nextCursor
        if ($hasMore -and [string]::IsNullOrWhiteSpace($cursor)) {
            throw "$Label returned hasMore=true without nextCursor"
        }
        if ($hasMore) {
            $separator = if ($InitialRelativePath.Contains('?')) { '&' } else { '?' }
            $relativePath = $InitialRelativePath + $separator + "cursor=" + [Uri]::EscapeDataString($cursor)
        }
        $page++
    } while ($hasMore)

    return @($items)
}

function Invoke-NumberedPagedGet {
    param(
        [Parameter(Mandatory = $true)][string] $Label,
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $FileStem,
        [int] $PageSize = 100,
        [ValidateSet("page", "pageNumber")][string] $PageParameter = "page",
        [switch] $CarrySnapshotReference
    )

    $items = New-Object System.Collections.ArrayList
    $page = 1
    $snapshotReference = $null
    do {
        if ($page -gt 10000) { throw "Pagination safety limit exceeded for $Label" }
        $separator = if ($Path.Contains('?')) { '&' } else { '?' }
        $relativePath = $Path + $separator + "$PageParameter=$page&pageSize=$PageSize"
        if ($CarrySnapshotReference -and $page -gt 1) {
            if ([string]::IsNullOrWhiteSpace($snapshotReference)) {
                throw "$Label requires snapshotReference for page $page"
            }
            $relativePath += "&snapshot=" + [Uri]::EscapeDataString($snapshotReference)
        }
        $file = "api/{0}-page-{1:D3}.json" -f $FileStem, $page
        $result = Invoke-ValidationGet -Label ("{0}-page-{1:D3}" -f $Label, $page) -RelativePath $relativePath -OutputFile $file
        if ($null -eq $result.Json -or $null -eq $result.Json.items) {
            throw "$Label did not return the required numbered-page items envelope"
        }
        foreach ($item in @($result.Json.items)) { [void]$items.Add($item) }
        $totalPages = [Math]::Max(1, [int]$result.Json.totalPages)
        if ($CarrySnapshotReference -and $page -eq 1) {
            $snapshotReference = [string]$result.Json.snapshotReference
            if ($totalPages -gt 1 -and [string]::IsNullOrWhiteSpace($snapshotReference)) {
                throw "$Label returned multiple pages without snapshotReference"
            }
        }
        $page++
    } while ($page -le $totalPages)

    return @($items)
}

try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "execution-log.md") -Destination (Join-Path $runDirectory "execution-log.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "signoff.md") -Destination (Join-Path $runDirectory "signoff.md")

    # Production V2 deliberately does not expose the incompatible legacy API/OpenAPI.
    # Every request below exercises a route registered by MapNewMesIngestEndpoints.
    [void](Invoke-ValidationGet -Label "contract-v2" -RelativePath "/api/v2/contract" -OutputFile "api/contract-v2.json")
    $pollRounds = New-Object System.Collections.ArrayList
    $lastPollTraceHighWater = $null
    :PollRoundLoop for ($round = 1; $round -le $PollSampleCount; $round++) {
        if ($round -gt 1 -and $PollSampleIntervalSeconds -gt 0) {
            Start-Sleep -Seconds $PollSampleIntervalSeconds
        }

        $waitDeadline = [DateTimeOffset]::UtcNow.AddSeconds($PollSampleWaitTimeoutSeconds)
        $attempt = 0
        while ($true) {
            $attempt++
            $attentionFile = "api/current-ingest-attention-round-{0:D3}-attempt-{1:D3}.json" -f $round, $attempt
            $attention = Invoke-ValidationGet -Label ("current-ingest-attention-round-{0:D3}-attempt-{1:D3}" -f $round, $attempt) -RelativePath "/api/v2/current-ingest-attention?kind=POLL_RUN_FAILURE&pageSize=1" -OutputFile $attentionFile
            $pollTraceHighWater = [long]$attention.Json.snapshot.pollTraceHighWater
            if ($pollTraceHighWater -gt 0 -and
                ($null -eq $lastPollTraceHighWater -or $pollTraceHighWater -gt $lastPollTraceHighWater)) {
                $failureItem = @($attention.Json.items | Where-Object { $_.kind -eq "POLL_RUN_FAILURE" }) | Select-Object -First 1
                $pollTraceId = if ($null -ne $failureItem -and [long]$failureItem.evidence.pollTraceSequence -eq $pollTraceHighWater) {
                    [string]$failureItem.evidence.pollTraceId
                } else {
                    # When the latest trace is SUCCESS, its projection commit is the
                    # operational snapshot identity. A latest non-success is exposed
                    # by the POLL_RUN_FAILURE item above.
                    [string]$attention.Json.snapshot.pollTraceId
                }
                $pollTraceFile = "api/poll-trace-round-{0:D3}.json" -f $round
                $pollTrace = Invoke-ValidationGet -Label ("poll-trace-round-{0:D3}" -f $round) -RelativePath ("/api/v2/poll-traces/" + [Uri]::EscapeDataString($pollTraceId)) -OutputFile $pollTraceFile
                $durationMs = [long][Math]::Max(0, ([DateTimeOffset]$pollTrace.Json.completedAt - [DateTimeOffset]$pollTrace.Json.startedAt).TotalMilliseconds)
                [void]$pollRounds.Add([PSCustomObject][ordered]@{
                    round = $round
                    captured_at = [string]$pollTrace.Json.completedAt
                    poll_trace_id = $pollTraceId
                    poll_trace_high_water = $pollTraceHighWater
                    query_version = [string]$pollTrace.Json.queryVersion
                    content_digest = [string]$pollTrace.Json.contentDigest
                    duration_ms = $durationMs
                    row_count = [long]$pollTrace.Json.rowCount
                    success = [string]::Equals([string]$pollTrace.Json.outcome, "SUCCESS", [StringComparison]::Ordinal)
                    outcome = [string]$pollTrace.Json.outcome
                    failure_stage = [string]$pollTrace.Json.diagnostic.stage
                    poll_trace_file = $pollTraceFile
                })
                $lastPollTraceHighWater = $pollTraceHighWater
                break
            }

            if ([DateTimeOffset]::UtcNow -ge $waitDeadline) {
                break PollRoundLoop
            }
            if ($PollSampleIntervalSeconds -gt 0) {
                Start-Sleep -Seconds $PollSampleIntervalSeconds
            } else {
                Start-Sleep -Milliseconds 250
            }
        }
    }

    # V2 demand-series uses a frozen snapshot plus numbered pages.
    $seriesPageProbeFirst = Invoke-ValidationGet -Label "demand-series-page-probe-first" -RelativePath "/api/v2/demand-series?page=1&pageSize=1" -OutputFile "api/demand-series-page-probe-first.json"
    $seriesSubsequentPageSampled = $false
    if ([int]$seriesPageProbeFirst.Json.totalPages -gt 1) {
        $snapshot = [Uri]::EscapeDataString([string]$seriesPageProbeFirst.Json.snapshotReference)
        [void](Invoke-ValidationGet -Label "demand-series-page-probe-subsequent" -RelativePath ("/api/v2/demand-series?page=2&pageSize=1&snapshot=$snapshot") -OutputFile "api/demand-series-page-probe-subsequent.json")
        $seriesSubsequentPageSampled = $true
    }

    $visibleSeries = Invoke-NumberedPagedGet -Label "demand-series-visible" -Path "/api/v2/demand-series?presence=VISIBLE" -FileStem "demand-series-visible" -CarrySnapshotReference

    $sampleDemandId = $null
    if ($visibleSeries.Count -gt 0) {
        $sampleDemandId = [string]$visibleSeries[0].currentDemandId
        [void](Invoke-ValidationGet -Label "demand-id-exact" -RelativePath ("/api/v2/readability-audit?demandId=" + [Uri]::EscapeDataString($sampleDemandId) + "&pageSize=1") -OutputFile "api/demand-id-exact.json")
    }

    $attentionItems = Invoke-NumberedPagedGet -Label "current-ingest-attention" -Path "/api/v2/current-ingest-attention" -FileStem "current-ingest-attention" -PageParameter "pageNumber"
    $goneSeries = Invoke-NumberedPagedGet -Label "demand-series-gone" -Path "/api/v2/demand-series?presence=GONE" -FileStem "demand-series-gone" -CarrySnapshotReference
    [void](Invoke-ValidationGet -Label "externally-readable-catalog" -RelativePath "/api/v2/externally-readable-demand-catalog" -OutputFile "api/externally-readable-demand-catalog.json")

    $dateSamples = @($visibleSeries | Group-Object workType | ForEach-Object {
        $item = $_.Group | Select-Object -First 1
        [PSCustomObject]@{
            task_type = [string]$item.workType
            demand_id = [string]$item.currentDemandId
            dates = [string]$item.liveMesFields.mesSourceDate
            step = [string]$item.liveMesFields.step
            mes_page_or_customer_it_reference = ""
            dates_is_current_step_entered_time = "PENDING"
            step_is_next_process = "PENDING"
            utc_plus_08_interpretation_confirmed = "PENDING"
            watch_local_display_confirmed = "PENDING"
            notes = ""
        }
    })
    $dateSamples | Export-Csv -LiteralPath (Join-Path $runDirectory "dates-samples.tsv") -Delimiter "`t" -NoTypeInformation -Encoding UTF8

    if ($WatchObservationSeconds -gt 0) {
        Write-Host "Keep MesIngest.Watch open at logical site $LogicalSite for $WatchObservationSeconds seconds..."
        Start-Sleep -Seconds $WatchObservationSeconds
    }

    $watchLines = New-Object System.Collections.ArrayList
    if (Test-Path -LiteralPath $WatchLogDirectory) {
        Get-ChildItem -LiteralPath $WatchLogDirectory -Filter "watch-latency-*.log" -File | ForEach-Object {
            Get-Content -LiteralPath $_.FullName | ForEach-Object {
                $line = $_
                if ($line -match '^recordedAt=(\S+) ') {
                    try {
                        $recordedAt = [DateTimeOffset]::Parse($Matches[1])
                        if ($recordedAt -ge $startedAt) { [void]$watchLines.Add((Protect-ValidationText $line)) }
                    } catch { }
                }
            }
        }
    }
    Set-Content -LiteralPath (Join-Path $runDirectory "watch-latency.log") -Value @($watchLines) -Encoding UTF8

    $correlationIds = @($metrics | ForEach-Object { [string]$_.correlation_id })
    $hostLatencyLines = New-Object System.Collections.ArrayList
    try {
        $eventParameters = @{
            FilterHashtable = @{ LogName = 'Application'; StartTime = $startedAt.LocalDateTime }
            ErrorAction = 'Stop'
        }
        if (-not [string]::IsNullOrWhiteSpace($HostEventComputerName)) {
            $eventParameters['ComputerName'] = $HostEventComputerName
        }
        Get-WinEvent @eventParameters |
            Where-Object {
                $message = [string]$_.Message
                if ($message -notmatch 'component=Host') { return $false }
                foreach ($id in $correlationIds) { if ($message.Contains($id)) { return $true } }
                return $false
            } |
            Sort-Object TimeCreated |
            ForEach-Object {
                [void]$hostLatencyLines.Add(("recordedAt={0:o} provider={1} eventId={2} {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, (Protect-ValidationText ([string]$_.Message).Replace("`r", " ").Replace("`n", " "))))
            }
    } catch {
        [void]$hostLatencyLines.Add("collection_error=" + (Protect-ValidationText $_.Exception.Message))
    }
    Set-Content -LiteralPath (Join-Path $runDirectory "host-latency.log") -Value @($hostLatencyLines) -Encoding UTF8

    $metricsPath = Join-Path $runDirectory "request-metrics.jsonl"
    $metrics | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content -LiteralPath $metricsPath -Encoding UTF8

    $hostLatencyText = @($hostLatencyLines) -join "`n"
    $hostEndpointSeen = $hostLatencyText.Contains("component=Host")
    $watchLatencySeen = $watchLines.Count -gt 0
    $responseCorrelationComplete = @($metrics | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.response_correlation_id) -or
        -not [string]::Equals([string]$_.correlation_id, [string]$_.response_correlation_id, [StringComparison]::Ordinal)
    }).Count -eq 0

    $thinProbeEvidence = Import-OracleProbeEvidence -ExpectedMode "Thin" -SourcePath $ThinProbeLog -OutputFile "probe-thin.txt"
    $thickProbeEvidence = Import-OracleProbeEvidence -ExpectedMode "Thick" -SourcePath $ThickProbeLog -OutputFile "probe-thick.txt"
    $liveOracleProbePassed = @(@($thinProbeEvidence, $thickProbeEvidence) | Where-Object {
        $_.result -eq "PASSED" -and
        $_.execution_scope -eq "LIVE_ORACLE" -and
        $_.connection_attempted -eq $true -and
        [string]::Equals([string]$_.requested_mode, [string]$_.actual_mode, [StringComparison]::OrdinalIgnoreCase) -and
        $_.query_id -ceq $canonicalQueryId -and
        $_.query_version -ceq $canonicalQueryVersion -and
        $_.query_sha256 -ceq $canonicalQuerySha256 -and
        [string]::Equals([string]$_.outcome, "Success", [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0
    $canonicalPollTraceIdentityComplete = $pollRounds.Count -gt 0 -and @($pollRounds | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.poll_trace_id) -or
        $_.poll_trace_high_water -le 0 -or
        -not [string]::Equals([string]$_.query_version, $canonicalQueryVersion, [StringComparison]::Ordinal) -or
        [string]$_.content_digest -notmatch '^[0-9a-f]{64}$' -or
        $_.row_count -lt 0 -or
        @("SUCCESS", "FAILURE", "INCOMPLETE") -notcontains [string]$_.outcome
    }).Count -eq 0

    $missingRequiredEvidence = New-Object System.Collections.ArrayList
    if (-not $hostEndpointSeen) { [void]$missingRequiredEvidence.Add("HOST_ENDPOINT_LATENCY") }
    if (-not $watchLatencySeen) { [void]$missingRequiredEvidence.Add("WATCH_TOTAL_LATENCY") }
    if (-not $responseCorrelationComplete) { [void]$missingRequiredEvidence.Add("CORRELATION_ID") }
    if ($pollRounds.Count -lt $PollSampleCount) { [void]$missingRequiredEvidence.Add("DISTINCT_POLL_ROUNDS") }
    if (-not $canonicalPollTraceIdentityComplete) { [void]$missingRequiredEvidence.Add("CANONICAL_POLL_TRACE_IDENTITY") }
    if (-not $liveOracleProbePassed) { [void]$missingRequiredEvidence.Add("LIVE_ORACLE_PROBE_PASSED") }
    if ([int]$seriesPageProbeFirst.Json.exactTotalCount -gt 1 -and -not $seriesSubsequentPageSampled) { [void]$missingRequiredEvidence.Add("DEMAND_SERIES_SUBSEQUENT_PAGE") }
    if ([string]::IsNullOrWhiteSpace($sampleDemandId)) { [void]$missingRequiredEvidence.Add("DEMAND_ID_EXACT") }

    if (-not [string]::IsNullOrWhiteSpace($sharedSecret)) {
        $secretLeak = Get-ChildItem -LiteralPath $runDirectory -Recurse -File |
            Select-String -SimpleMatch -Pattern $sharedSecret -List -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $secretLeak) {
            throw "SharedSecret material was detected in validation evidence; quarantine and delete this run directory."
        }
    }

    $captureStatus = if ($missingRequiredEvidence.Count -eq 0) {
        "technical-capture-completed"
    } else {
        "technical-capture-incomplete"
    }
    $manifest = [ordered]@{
        schema_version = 2
        run_id = $runId
        experiment_id = "mes-ingest-factory-validation"
        created_at = $startedAt.ToString("o")
        completed_at = [DateTimeOffset]::UtcNow.ToString("o")
        status = $captureStatus
        timezone = [TimeZoneInfo]::Local.Id
        environment = [ordered]@{
            platform = [Environment]::OSVersion.VersionString
            logical_site = $LogicalSite
            shared_secret_source = $SharedSecretEnvironmentVariable
            shared_secret_present = -not [string]::IsNullOrWhiteSpace($sharedSecret)
        }
        probe = [ordered]@{
            thin = $thinProbeEvidence
            thick = $thickProbeEvidence
        }
        formal_source_evidence = [ordered]@{
            canonical_poll_trace_identity_complete = $canonicalPollTraceIdentityComplete
            live_oracle_probe_passed = $liveOracleProbePassed
        }
        poll_rounds = @($pollRounds)
        request_metrics = [ordered]@{
            file = "request-metrics.jsonl"
            count = $metrics.Count
            timeout_seconds = $RequestTimeoutSeconds
            poll_sample_count = $PollSampleCount
            distinct_poll_sample_count = $pollRounds.Count
            exact_demand_id_sampled = -not [string]::IsNullOrWhiteSpace($sampleDemandId)
            subsequent_demand_page_sampled = $seriesSubsequentPageSampled
            response_correlation_complete = $responseCorrelationComplete
            visible_rows = $visibleSeries.Count
            gone_rows = $goneSeries.Count
            attention_rows = $attentionItems.Count
            poll_trace_high_watermark = if ($null -eq $lastPollTraceHighWater) { 0 } else { $lastPollTraceHighWater }
        }
        dates_semantics = [ordered]@{
            samples_file = "dates-samples.tsv"
            task_type_sample_count = $dateSamples.Count
            customer_confirmation = "PENDING"
            watch_local_display_confirmation = "PENDING"
        }
        latency_evidence = [ordered]@{
            host_file = "host-latency.log"
            watch_file = "watch-latency.log"
            host_line_count = $hostLatencyLines.Count
            watch_line_count = $watchLines.Count
            host_endpoint_seen = $hostEndpointSeen
            watch_total_seen = $watchLatencySeen
            missing_required_evidence = @($missingRequiredEvidence)
        }
        manual_checks = [ordered]@{
            dates_semantics_confirmed = $false
            watch_timezone_display_confirmed = $false
            authenticated_v2_get_confirmed = $false
            v2_endpoint_timeout_reviewed = $false
        }
        safety = [ordered]@{
            http_methods = @("GET")
            direct_database_connection = $false
            oracle_ddl_or_query_rewrite = $false
        }
        redaction = [ordered]@{
            contains_secret_material = $false
            contains_filled_local_config = $false
            notes = "SharedSecret read from process environment only; evidence stores logical site and URL paths, not BaseUrl."
        }
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runDirectory "run-manifest.json") -Encoding UTF8

    Get-ChildItem -LiteralPath $runDirectory -Recurse -File |
        Where-Object { $_.Name -ne "sha256.txt" } |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($runDirectory.Length + 1).Replace('\', '/')
            "{0} *{1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
        } | Set-Content -LiteralPath (Join-Path $runDirectory "sha256.txt") -Encoding UTF8

    Write-Host "Factory validation evidence captured: $runDirectory"
    Write-Host "Next: complete dates-samples.tsv and execution-log.md with customer/MES and Watch confirmations."
} finally {
    $sharedSecret = $null
}
