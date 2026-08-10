#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ArtifactsDirectory,

    [ValidateRange(5, 30)]
    [int] $StartupTimeoutSeconds = 10,

    [switch] $SkipWatch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$serviceExecutable = Join-Path $packageRoot 'service\MesIngest.Host.exe'
$watchExecutable = Join-Path $packageRoot 'watch\MesIngest.Watch.exe'
$staticOpenApiPath = Join-Path $packageRoot 'openapi\v1.json'
foreach ($path in @($serviceExecutable, $staticOpenApiPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release smoke input missing: $path" }
}
if (-not $SkipWatch -and -not (Test-Path -LiteralPath $watchExecutable -PathType Leaf)) {
    throw "Release smoke input missing: $watchExecutable"
}

$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path ([IO.Path]::GetTempPath()) ("mes-ingest-release-smoke-" + [Guid]::NewGuid().ToString('N'))
} else {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$sandbox = Join-Path $artifacts 'sandbox'
$localData = Join-Path $sandbox 'LocalAppData'
New-Item -ItemType Directory -Path $localData -Force | Out-Null
$csvPath = Join-Path $sandbox 'snapshot.csv'
@'
TASK_TYPE,SUBLOT,AREA,EQP,STEP,DATES,PACKAGE
DIE_TO_OVEN,RELEASE-SMOKE,N01-01,WB-01,烘箱,2026-08-10T09:00:00+08:00,PKG-SMOKE
'@ | Set-Content -LiteralPath $csvPath -Encoding UTF8

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$hostProcess = $null
$watchProcess = $null
$startedAt = [DateTimeOffset]::UtcNow
try {
    $hostInfo = [Diagnostics.ProcessStartInfo]::new()
    $hostInfo.FileName = $serviceExecutable
    $hostInfo.WorkingDirectory = Split-Path -Parent $serviceExecutable
    $hostInfo.UseShellExecute = $false
    $hostInfo.RedirectStandardOutput = $true
    $hostInfo.RedirectStandardError = $true
    $hostInfo.EnvironmentVariables['MesIngest__SnapshotSource'] = 'File'
    $hostInfo.EnvironmentVariables['MesIngest__SnapshotCsvPath'] = $csvPath
    $hostInfo.EnvironmentVariables['MesIngest__SqlServerConnectionString'] = ''
    $hostInfo.EnvironmentVariables['MesIngest__RunOneShotOnStartup'] = 'true'
    $hostInfo.EnvironmentVariables['MesIngest__ContinuousPollEnabled'] = 'false'
    $hostInfo.EnvironmentVariables['MesIngest__Urls'] = $baseUrl
    $hostInfo.EnvironmentVariables['MesIngest__SharedSecret'] = ''
    $hostProcess = [Diagnostics.Process]::Start($hostInfo)
    if ($null -eq $hostProcess) { throw 'Packaged Host did not start.' }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $contract = $null
    do {
        if ($hostProcess.HasExited) { throw "Packaged Host exited during startup with code $($hostProcess.ExitCode)." }
        try {
            $contract = Invoke-RestMethod -Uri "$baseUrl/api/contract" -TimeoutSec 2
        } catch {
            Start-Sleep -Milliseconds 200
        }
    } while ($null -eq $contract -and [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $contract) { throw "Packaged Host did not expose /api/contract within $StartupTimeoutSeconds seconds." }

    $liveOpenApiText = (Invoke-WebRequest -Uri "$baseUrl/openapi/v1.json" -TimeoutSec 5).Content
    $liveOpenApiText | Set-Content -LiteralPath (Join-Path $artifacts 'live-openapi.json') -Encoding UTF8
    $liveOpenApi = $liveOpenApiText | ConvertFrom-Json
    $staticOpenApi = (Get-Content -Raw -LiteralPath $staticOpenApiPath) | ConvertFrom-Json
    $liveContract = $liveOpenApi | ConvertTo-Json -Depth 100 -Compress
    $staticContract = $staticOpenApi | ConvertTo-Json -Depth 100 -Compress
    if ($liveContract -cne $staticContract) { throw 'Packaged runtime OpenAPI differs from the complete offline OpenAPI contract.' }

    $demands = Invoke-RestMethod -Uri "$baseUrl/api/demands?status=VISIBLE" -TimeoutSec 5
    $alerts = Invoke-RestMethod -Uri "$baseUrl/api/alerts?active=true" -TimeoutSec 5
    $pollHealth = Invoke-RestMethod -Uri "$baseUrl/api/poll-health" -TimeoutSec 5
    $changes = Invoke-RestMethod -Uri "$baseUrl/api/demand-changes?afterSequence=0&limit=100" -TimeoutSec 5
    if (@($demands.items).Count -ne 1 -or $demands.items[0].sublot -ne 'RELEASE-SMOKE') {
        throw 'Packaged Host did not project the release-smoke TransportDemand.'
    }

    $watchStartupMs = $null
    if (-not $SkipWatch) {
        $preferenceDirectory = Join-Path $localData 'MesIngest.Watch'
        New-Item -ItemType Directory -Path $preferenceDirectory -Force | Out-Null
        '{"version":1,"windowWidth":1333,"windowHeight":777,"demandShare":0.65}' |
            Set-Content -LiteralPath (Join-Path $preferenceDirectory 'layout-preferences.json') -Encoding UTF8
        $watchInfo = [Diagnostics.ProcessStartInfo]::new()
        $watchInfo.FileName = $watchExecutable
        $watchInfo.WorkingDirectory = Split-Path -Parent $watchExecutable
        $watchInfo.UseShellExecute = $false
        $watchInfo.RedirectStandardOutput = $true
        $watchInfo.RedirectStandardError = $true
        $watchInfo.EnvironmentVariables['LOCALAPPDATA'] = $localData
        $watchInfo.EnvironmentVariables['MesIngestWatch__BaseUrl'] = $baseUrl
        $watchInfo.EnvironmentVariables['MesIngestWatch__RenderingMode'] = 'SoftwareOnly'
        $watchInfo.EnvironmentVariables['MesIngestWatch__RequestTimeoutSeconds'] = '5'
        $watchInfo.EnvironmentVariables['MesIngestWatch__LogDirectory'] = (Join-Path $sandbox 'watch-logs')
        $watchStartedAt = [Diagnostics.Stopwatch]::StartNew()
        $watchProcess = [Diagnostics.Process]::Start($watchInfo)
        if ($null -eq $watchProcess) { throw 'Packaged Watch did not start.' }
        do {
            if ($watchProcess.HasExited) { throw "Packaged Watch exited during startup with code $($watchProcess.ExitCode)." }
            $watchProcess.Refresh()
            if ($watchProcess.MainWindowHandle -ne [IntPtr]::Zero -and $watchProcess.Responding) { break }
            Start-Sleep -Milliseconds 200
        } while ($watchStartedAt.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds)
        if ($watchProcess.MainWindowHandle -eq [IntPtr]::Zero -or -not $watchProcess.Responding) {
            throw "Packaged Watch did not expose a responsive main window within $StartupTimeoutSeconds seconds."
        }
        $watchStartupMs = [Math]::Round($watchStartedAt.Elapsed.TotalMilliseconds, 1)

        Add-Type -AssemblyName UIAutomationClient
        $window = [Windows.Automation.AutomationElement]::FromHandle($watchProcess.MainWindowHandle)
        if ($window.Current.BoundingRectangle.Width -lt 1250) {
            throw 'Packaged Watch did not load the isolated LocalApplicationData layout preference.'
        }
        $requiredNames = @(
            '概览健康结论',
            'Host 接入状态',
            '最近轮询健康',
            '查看活动 IngestAlert 默认查询',
            '查看 VISIBLE TransportDemand 默认查询'
        )
        foreach ($name in $requiredNames) {
            $condition = [Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::NameProperty,
                $name)
            $element = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -eq $element) { throw "Packaged Watch overview is missing UI Automation element: $name" }
        }

        $watchLogDirectory = Join-Path $sandbox 'watch-logs'
        $logDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
        do {
            $latencyLogs = @(Get-ChildItem -LiteralPath $watchLogDirectory -Filter 'watch-latency-*.log' -File -ErrorAction SilentlyContinue)
            if ($latencyLogs.Count -gt 0 -and $latencyLogs[0].Length -gt 0) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTimeOffset]::UtcNow -lt $logDeadline)
        if ($latencyLogs.Count -eq 0 -or $latencyLogs[0].Length -eq 0) {
            throw 'Packaged Watch did not write latency evidence under the isolated LocalApplicationData sandbox.'
        }
    }

    [ordered]@{
        status = 'PASSED'
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        durationMs = [Math]::Round(([DateTimeOffset]::UtcNow - $startedAt).TotalMilliseconds, 1)
        watchStartupMs = $watchStartupMs
        baseUrl = 'http://127.0.0.1:<ephemeral>'
        contractVersion = $contract.contractVersion
        schemaVersion = $contract.schemaVersion
        visibleDemandCount = @($demands.items).Count
        activeAlertCount = @($alerts.items).Count
        pollSuccess = $pollHealth.success
        changeCount = @($changes.items).Count
        openApiMatched = $true
        watchValidated = -not $SkipWatch
        isolatedPreferenceLoaded = -not $SkipWatch
        isolatedLogWritten = -not $SkipWatch
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifacts 'release-smoke-result.json') -Encoding UTF8
    Write-Output "MESINGEST_RELEASE_SMOKE_PASSED: artifacts=$artifacts"
}
finally {
    foreach ($process in @($watchProcess, $hostProcess)) {
        if ($null -ne $process) {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
            $name = if ($process -eq $watchProcess) { 'watch' } else { 'host' }
            $process.StandardOutput.ReadToEnd() | Set-Content -LiteralPath (Join-Path $artifacts "$name.stdout.log") -Encoding UTF8
            $process.StandardError.ReadToEnd() | Set-Content -LiteralPath (Join-Path $artifacts "$name.stderr.log") -Encoding UTF8
            $process.Dispose()
        }
    }
}
