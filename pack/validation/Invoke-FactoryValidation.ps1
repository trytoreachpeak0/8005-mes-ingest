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

    [string] $HostEventComputerName = ""
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

function Protect-ValidationText {
    param([AllowEmptyString()][string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $clean = $Text -replace '(?i)(Authorization\s*:\s*Bearer\s+)\S+', '$1[redacted]'
    $clean = $clean -replace '(?i)((?:SharedSecret|Password|Pwd)\s*[:=]\s*)[^;\s]+', '$1[redacted]'
    $clean = $clean -replace '(?i)((?:Data Source|Server|Initial Catalog|Database|User ID|UID|DSN)\s*=\s*)[^;\s]+', '$1[redacted]'
    return $clean
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

try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "execution-log.md") -Destination (Join-Path $runDirectory "execution-log.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "signoff.md") -Destination (Join-Path $runDirectory "signoff.md")

    # Documentation is public; all /api requests still carry SharedSecret from the environment when set.
    [void](Invoke-ValidationGet -Label "swagger" -RelativePath "/swagger/index.html" -OutputFile "api/swagger.html")
    [void](Invoke-ValidationGet -Label "openapi" -RelativePath "/openapi/v1.json" -OutputFile "api/openapi-v1.json")
    [void](Invoke-ValidationGet -Label "contract" -RelativePath "/api/contract" -OutputFile "api/contract.json")
    $pollRounds = New-Object System.Collections.ArrayList
    $lastPollEndedAt = $null
    :PollRoundLoop for ($round = 1; $round -le $PollSampleCount; $round++) {
        if ($round -gt 1 -and $PollSampleIntervalSeconds -gt 0) {
            Start-Sleep -Seconds $PollSampleIntervalSeconds
        }

        $waitDeadline = [DateTimeOffset]::UtcNow.AddSeconds($PollSampleWaitTimeoutSeconds)
        $attempt = 0
        while ($true) {
            $attempt++
            $pollHealthFile = "api/poll-health-round-{0:D3}-attempt-{1:D3}.json" -f $round, $attempt
            $pollHealth = Invoke-ValidationGet -Label ("poll-health-round-{0:D3}-attempt-{1:D3}" -f $round, $attempt) -RelativePath "/api/poll-health" -OutputFile $pollHealthFile
            $pollEndedAt = [string]$pollHealth.Json.endedAt
            if (-not [string]::IsNullOrWhiteSpace($pollEndedAt) -and
                ($null -eq $lastPollEndedAt -or -not [string]::Equals($lastPollEndedAt, $pollEndedAt, [StringComparison]::Ordinal))) {
                [void]$pollRounds.Add([PSCustomObject][ordered]@{
                    round = $round
                    captured_at = $pollEndedAt
                    duration_ms = $pollHealth.Json.durationMs
                    oracle_duration_ms = $pollHealth.Json.oracleDurationMs
                    row_count = $pollHealth.Json.rowCount
                    success = $pollHealth.Json.success
                    outcome = [string]$pollHealth.Json.outcome
                    failure_stage = [string]$pollHealth.Json.failureStage
                    poll_health_file = $pollHealthFile
                })
                $lastPollEndedAt = $pollEndedAt
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

    # Always probe a real first/subsequent cursor pair when at least two VISIBLE rows exist.
    $demandPageProbeFirst = Invoke-ValidationGet -Label "demands-page-probe-first" -RelativePath "/api/demands?status=VISIBLE&sortBy=dates&direction=desc&limit=1" -OutputFile "api/demands-page-probe-first.json"
    $demandSubsequentPageSampled = $false
    if ([bool]$demandPageProbeFirst.Json.hasMore -and -not [string]::IsNullOrWhiteSpace([string]$demandPageProbeFirst.Json.nextCursor)) {
        $probeCursor = [Uri]::EscapeDataString([string]$demandPageProbeFirst.Json.nextCursor)
        [void](Invoke-ValidationGet -Label "demands-page-probe-subsequent" -RelativePath ("/api/demands?status=VISIBLE&sortBy=dates&direction=desc&limit=1&cursor=$probeCursor") -OutputFile "api/demands-page-probe-subsequent.json")
        $demandSubsequentPageSampled = $true
    }

    $visibleItems = Invoke-PagedGet -Label "demands-visible" -InitialRelativePath "/api/demands?status=VISIBLE&sortBy=dates&direction=desc&limit=100" -FileStem "demands-visible"

    $sampleDemandId = $null
    if ($visibleItems.Count -gt 0) {
        $sampleDemandId = [string]$visibleItems[0].demandId
        [void](Invoke-ValidationGet -Label "demand-id-exact" -RelativePath ("/api/demands/" + [Uri]::EscapeDataString($sampleDemandId)) -OutputFile "api/demand-id-exact.json")
        $prefixLength = [Math]::Min(6, $sampleDemandId.Length)
        $prefix = $sampleDemandId.Substring(0, $prefixLength).ToLowerInvariant()
        [void](Invoke-ValidationGet -Label "demand-id-prefix" -RelativePath ("/api/demands?demandId=" + [Uri]::EscapeDataString($prefix) + "&limit=100") -OutputFile "api/demand-id-prefix.json")
    }

    $alertItems = Invoke-PagedGet -Label "alerts" -InitialRelativePath "/api/alerts?limit=100" -FileStem "alerts"

    $changeFeed = Invoke-ValidationGet -Label "demand-changes-initial" -RelativePath "/api/demand-changes?afterSequence=0&limit=100" -OutputFile "api/demand-changes-initial.json" -AllowedStatusCodes @(200, 410)
    $highWatermark = 0
    if ($null -ne $changeFeed.Json -and $null -ne $changeFeed.Json.highWatermark) {
        $highWatermark = [long]$changeFeed.Json.highWatermark
    }

    # Authoritative Bootstrap measurement: current VISIBLE + last-24h GONE, then catch up after its watermark.
    $goneAtFrom = [Uri]::EscapeDataString(([DateTimeOffset]::UtcNow.AddHours(-24).ToString("o")))
    $goneItems = Invoke-PagedGet -Label "bootstrap-gone" -InitialRelativePath ("/api/demands?status=GONE&goneAtFrom=$goneAtFrom&sortBy=goneAt&direction=asc&limit=100") -FileStem "bootstrap-gone"
    [void](Invoke-ValidationGet -Label "bootstrap-catch-up" -RelativePath ("/api/demand-changes?afterSequence=$highWatermark&limit=100") -OutputFile "api/bootstrap-catch-up.json")

    $dateSamples = @($visibleItems | Group-Object taskType | ForEach-Object {
        $item = $_.Group | Select-Object -First 1
        [PSCustomObject]@{
            task_type = [string]$item.taskType
            demand_id = [string]$item.demandId
            dates = [string]$item.dates
            step = [string]$item.step
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
                if ($message -notmatch 'component=(Host|SqlServer|Oracle)') { return $false }
                if ($message -match 'stage=(ORACLE_QUERY|SQL_QUERY|SQL_WRITE|SQL_TRANSACTION|SQL_OPEN)') { return $true }
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
    $oracleQuerySeen = $hostLatencyText.Contains("ORACLE_QUERY")
    $sqlQuerySeen = $hostLatencyText.Contains("SQL_QUERY")
    $sqlWriteSeen = $hostLatencyText.Contains("SQL_WRITE")
    $watchLatencySeen = $watchLines.Count -gt 0
    $responseCorrelationComplete = @($metrics | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.response_correlation_id) -or
        -not [string]::Equals([string]$_.correlation_id, [string]$_.response_correlation_id, [StringComparison]::Ordinal)
    }).Count -eq 0

    $missingRequiredEvidence = New-Object System.Collections.ArrayList
    if (-not $hostEndpointSeen) { [void]$missingRequiredEvidence.Add("HOST_ENDPOINT_LATENCY") }
    if (-not $oracleQuerySeen) { [void]$missingRequiredEvidence.Add("ORACLE_QUERY") }
    if (-not $sqlQuerySeen) { [void]$missingRequiredEvidence.Add("SQL_QUERY") }
    if (-not $sqlWriteSeen) { [void]$missingRequiredEvidence.Add("SQL_WRITE") }
    if (-not $watchLatencySeen) { [void]$missingRequiredEvidence.Add("WATCH_TOTAL_LATENCY") }
    if (-not $responseCorrelationComplete) { [void]$missingRequiredEvidence.Add("CORRELATION_ID") }
    if ($pollRounds.Count -lt $PollSampleCount) { [void]$missingRequiredEvidence.Add("DISTINCT_POLL_ROUNDS") }
    if (-not $demandSubsequentPageSampled) { [void]$missingRequiredEvidence.Add("DEMANDS_SUBSEQUENT_PAGE") }
    if ([string]::IsNullOrWhiteSpace($sampleDemandId)) { [void]$missingRequiredEvidence.Add("DEMAND_ID_EXACT_PREFIX") }

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
            thin = [ordered]@{ attempted = $false; result = "PENDING"; log = "probe-thin.txt" }
            thick = [ordered]@{ attempted = $false; result = "PENDING"; log = "probe-thick.txt" }
        }
        poll_rounds = @($pollRounds)
        request_metrics = [ordered]@{
            file = "request-metrics.jsonl"
            count = $metrics.Count
            timeout_seconds = $RequestTimeoutSeconds
            poll_sample_count = $PollSampleCount
            distinct_poll_sample_count = $pollRounds.Count
            exact_demand_id_sampled = -not [string]::IsNullOrWhiteSpace($sampleDemandId)
            subsequent_demand_page_sampled = $demandSubsequentPageSampled
            response_correlation_complete = $responseCorrelationComplete
            visible_rows = $visibleItems.Count
            gone_last_24h_rows = $goneItems.Count
            alert_rows = $alertItems.Count
            bootstrap_high_watermark = $highWatermark
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
            oracle_query_seen = $oracleQuerySeen
            sql_query_seen = $sqlQuerySeen
            sql_write_seen = $sqlWriteSeen
            watch_total_seen = $watchLatencySeen
            missing_required_evidence = @($missingRequiredEvidence)
        }
        manual_checks = [ordered]@{
            dates_semantics_confirmed = $false
            watch_timezone_display_confirmed = $false
            swagger_authorized_get_confirmed = $false
            endpoint_stage_timeout_reviewed = $false
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
