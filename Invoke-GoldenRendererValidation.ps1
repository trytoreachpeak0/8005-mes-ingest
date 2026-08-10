[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Ticket,

    [ValidateSet(
        'all',
        'watch-vm-tests',
        'watch-xaml-visual',
        'watch-xaml-stability',
        'watch-ui-journeys',
        'watch-window-visual',
        'watch-window-stability',
        'watch-window-promoted-stability',
        'watch-ui-stability',
        'watch-package-release')]
    [string]$Suite = 'watch-vm-tests',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(1, 100)]
    [int]$Runs = 10,

    [ValidateSet(96, 120, 144)]
    [int]$ExpectedDpi = 96,

    [ValidateRange(1, 16384)]
    [int]$ExpectedDesktopWidth = 1920,

    [ValidateRange(1, 16384)]
    [int]$ExpectedDesktopHeight = 1080,

    [string]$VmName = 'gpt_win11',

    [string]$SourceDirectory = $PSScriptRoot,

    [string]$ArtifactsDirectory,

    [ValidateRange(60, 14400)]
    [int]$TimeoutSeconds = 3600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$requiredFiles = @(
    'Invoke-WatchUiTests.ps1',
    'WatchBaselineTools.ps1',
    'Test-WatchXamlBaselineStability.ps1',
    'Test-WatchWindowBaselineStability.ps1',
    'Test-WatchUiGateStability.ps1',
    'Test-GoldenRendererEnvironment.ps1',
    'MesIngest.Watch.UiTests\MesIngest.Watch.UiTests.csproj',
    'pack\Publish-MesIngest.ps1',
    'pack\Test-ReleasePackage.ps1',
    'pack\validation\Invoke-ReleaseSmoke.ps1',
    'pack\validation\Invoke-WatchAcceptance.ps1'
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) {
        throw "Golden renderer source is missing: $required"
    }
}

$credentialPath = Join-Path $env:LOCALAPPDATA 'MesIngestWatch\gpt_win11.credential.xml'
if (-not (Test-Path -LiteralPath $credentialPath)) {
    throw "PowerShell Direct credential not found: $credentialPath"
}
if ((Get-VM -Name $VmName -ErrorAction Stop).State -ne 'Running') {
    throw "Golden renderer VM is not running: $VmName"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runName = "run-$stamp-$Suite"
$guestRoot = "C:\MesIngest\$Ticket\$runName"
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    Join-Path $source ".artifacts\golden-renderer\ticket-$Ticket\$runName"
} else {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
}
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

$gitRoot = @(& git -C $source rev-parse --show-toplevel 2>$null)
$gitCommit = if ($LASTEXITCODE -eq 0) {
    (@(& git -C $source rev-parse HEAD 2>$null) | Select-Object -First 1)
} else {
    $null
}
$gitStatus = if ($null -ne $gitCommit) {
    @(& git -C $source status --porcelain=v1 --untracked-files=normal -- . 2>$null)
} else {
    @()
}

$tempBase = Join-Path $env:TEMP 'MesIngestGoldenRenderer'
$tempRoot = Join-Path $tempBase ([Guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $tempRoot 'payload\Source\mes\ingest\csharp'
$payloadZip = Join-Path $tempRoot 'payload.zip'
$runnerPath = Join-Path $tempRoot 'run-golden-validation.ps1'
$taskName = "MesIngestWatch-Golden-$Ticket-$stamp"
$session = $null

try {
    New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null
    & robocopy $source $payloadRoot /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP `
        /XD bin obj TestResults .artifacts | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "robocopy failed with exit code $LASTEXITCODE"
    }
    if ($Suite -eq 'watch-package-release') {
        $packagePayload = Join-Path $tempRoot 'payload\ReleasePackage\MesIngest'
        $packageBuildLog = Join-Path $artifacts 'host-package-build.log'
        & (Join-Path $source 'pack\Publish-MesIngest.ps1') `
            -OutputDir $packagePayload `
            -Configuration $Configuration `
            -Runtime 'win-x64' 2>&1 | Tee-Object -FilePath $packageBuildLog
        if ($LASTEXITCODE -ne 0) {
            throw "Host release package build failed with exit code $LASTEXITCODE."
        }
    }
    Compress-Archive -Path (Join-Path $tempRoot 'payload\*') `
        -DestinationPath $payloadZip -CompressionLevel Fastest

    $payloadHash = (Get-FileHash -LiteralPath $payloadZip -Algorithm SHA256).Hash
    [ordered]@{
        Ticket = $Ticket
        Suite = $Suite
        Configuration = $Configuration
        RequestedRuns = if ($Suite -like '*-stability') { $Runs } else { 1 }
        VmName = $VmName
        GuestRunDirectory = $guestRoot
        CreatedAt = [DateTimeOffset]::Now.ToString('O')
        SourceDirectory = $source
        GitRoot = @($gitRoot) | Select-Object -First 1
        GitCommit = $gitCommit
        GitDirty = $gitStatus.Count -gt 0
        GitStatus = $gitStatus
        PayloadSha256 = $payloadHash
        ExpectedEnvironment = [ordered]@{
            DesktopWidth = $ExpectedDesktopWidth
            DesktopHeight = $ExpectedDesktopHeight
            Dpi = $ExpectedDpi
        }
    } | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $artifacts 'host-manifest.json') -Encoding utf8

    @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Suite,
    [Parameter(Mandatory = $true)][string]$Configuration,
    [Parameter(Mandatory = $true)][int]$Runs,
    [Parameter(Mandatory = $true)][int]$ExpectedDpi,
    [Parameter(Mandatory = $true)][int]$ExpectedDesktopWidth,
    [Parameter(Mandatory = $true)][int]$ExpectedDesktopHeight
)

$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $Root 'Results\result.json'
$logPath = Join-Path $Root 'Results\validation.log'
try {
    $source = Join-Path $Root 'Source\mes\ingest\csharp'
    New-Item -ItemType Directory -Path (Join-Path $Root 'Results') -Force | Out-Null
    Set-Location -LiteralPath $source
    $env:NUGET_PACKAGES = 'C:\MesIngest\Ticket11\NuGetPackages'
    $env:NUGET_XMLDOC_MODE = 'skip'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:MesIngestWatch__RenderingMode = 'SoftwareOnly'

    & '.\Test-GoldenRendererEnvironment.ps1' `
        -ExpectedDpi $ExpectedDpi `
        -ExpectedDesktopWidth $ExpectedDesktopWidth `
        -ExpectedDesktopHeight $ExpectedDesktopHeight `
        -OutputPath (Join-Path $Root 'Results\environment.json') 2>&1 |
        Tee-Object -FilePath $logPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Suite -eq 'watch-package-release') {
        $packageBuild = Join-Path $Root 'ReleasePackage\MesIngest'
        $installRoot = Join-Path $Root 'CleanInstall\MesIngest'
        New-Item -ItemType Directory -Path (Split-Path -Parent $installRoot) -Force | Out-Null
        Copy-Item -LiteralPath $packageBuild -Destination $installRoot -Recurse
        & (Join-Path $installRoot 'validation\Invoke-ReleaseSmoke.ps1') `
            -ArtifactsDirectory (Join-Path $Root 'Results\release-smoke') 2>&1 |
            Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        & (Join-Path $installRoot 'validation\Invoke-WatchAcceptance.ps1') `
            -HarnessRoot $source `
            -Suite all `
            -Configuration $Configuration `
            -ArtifactsDirectory (Join-Path $Root 'Results\packaged-watch-acceptance') 2>&1 |
            Tee-Object -FilePath $logPath -Append
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        $releaseEvidence = Join-Path $Root 'Results\release-package'
        New-Item -ItemType Directory -Path $releaseEvidence -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $installRoot 'VERSION.txt') -Destination $releaseEvidence
        Copy-Item -LiteralPath (Join-Path $installRoot 'RELEASE-MANIFEST.json') -Destination $releaseEvidence
        Compress-Archive -Path (Join-Path $installRoot '*') `
            -DestinationPath (Join-Path $releaseEvidence 'MesIngest-win-x64.zip') `
            -CompressionLevel Fastest
    }
    elseif ($Suite -eq 'watch-xaml-stability') {
        & '.\Test-WatchXamlBaselineStability.ps1' `
            -Configuration $Configuration `
            -Runs $Runs 2>&1 | Tee-Object -FilePath $logPath -Append
    }
    elseif ($Suite -eq 'watch-window-stability') {
        & '.\Test-WatchWindowBaselineStability.ps1' `
            -Configuration $Configuration `
            -Runs $Runs `
            -Mode Candidate `
            -ArtifactsDirectory (Join-Path $Root 'Results\watch-window-stability') 2>&1 |
            Tee-Object -FilePath $logPath -Append
    }
    elseif ($Suite -eq 'watch-window-promoted-stability') {
        & '.\Test-WatchWindowBaselineStability.ps1' `
            -Configuration $Configuration `
            -Runs $Runs `
            -Mode Promoted `
            -ArtifactsDirectory (Join-Path $Root 'Results\watch-window-promoted-stability') 2>&1 |
            Tee-Object -FilePath $logPath -Append
    }
    elseif ($Suite -eq 'watch-ui-stability') {
        & '.\Test-WatchUiGateStability.ps1' `
            -Configuration $Configuration `
            -Runs $Runs `
            -ArtifactsDirectory (Join-Path $Root 'Results\watch-ui-stability') 2>&1 |
            Tee-Object -FilePath $logPath -Append
    }
    else {
        & '.\Invoke-WatchUiTests.ps1' `
            -Configuration $Configuration `
            -Suite $Suite 2>&1 | Tee-Object -FilePath $logPath -Append
    }
    $exitCode = $LASTEXITCODE
    [pscustomobject]@{
        Status = if ($exitCode -eq 0) { 'PASSED' } else { 'FAILED' }
        ExitCode = $exitCode
        CompletedAt = [DateTimeOffset]::Now.ToString('O')
        Suite = $Suite
        Runs = if ($Suite -like '*-stability') { $Runs } else { 1 }
    } | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding utf8
    exit $exitCode
}
catch {
    New-Item -ItemType Directory -Path (Join-Path $Root 'Results') -Force | Out-Null
    [pscustomobject]@{
        Status = 'FAILED'
        ExitCode = 1
        CompletedAt = [DateTimeOffset]::Now.ToString('O')
        Error = $_.Exception.ToString()
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding utf8
    exit 1
}
'@ | Set-Content -LiteralPath $runnerPath -Encoding utf8

    $credential = Import-Clixml -LiteralPath $credentialPath
    $session = New-PSSession -VMName $VmName -Credential $credential
    Invoke-Command -Session $session -ScriptBlock {
        param($root)
        if (Test-Path -LiteralPath $root) {
            throw "Guest run directory already exists: $root"
        }
        New-Item -ItemType Directory -Path $root | Out-Null
    } -ArgumentList $guestRoot
    Copy-Item -ToSession $session -LiteralPath $payloadZip `
        -Destination (Join-Path $guestRoot 'payload.zip')
    Copy-Item -ToSession $session -LiteralPath $runnerPath `
        -Destination (Join-Path $guestRoot 'run-golden-validation.ps1')

    Invoke-Command -Session $session -ScriptBlock {
        param($root, $task, $suite, $configuration, $runs, $dpi, $width, $height)
        Expand-Archive -LiteralPath (Join-Path $root 'payload.zip') -DestinationPath $root
        $runner = Join-Path $root 'run-golden-validation.ps1'
        $arguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA',
            '-File', "`"$runner`"",
            '-Root', "`"$root`"",
            '-Suite', $suite,
            '-Configuration', $configuration,
            '-Runs', $runs,
            '-ExpectedDpi', $dpi,
            '-ExpectedDesktopWidth', $width,
            '-ExpectedDesktopHeight', $height
        ) -join ' '
        $action = New-ScheduledTaskAction `
            -Execute 'C:\Program Files\PowerShell\7\pwsh.exe' `
            -Argument $arguments
        $principal = New-ScheduledTaskPrincipal `
            -UserId 'GPT-WIN11\gpt' `
            -LogonType Interactive `
            -RunLevel Highest
        Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force | Out-Null
        Start-ScheduledTask -TaskName $task
    } -ArgumentList @(
        $guestRoot, $taskName, $Suite, $Configuration, $Runs,
        $ExpectedDpi, $ExpectedDesktopWidth, $ExpectedDesktopHeight)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 5
        $state = Invoke-Command -Session $session -ScriptBlock {
            param($task)
            (Get-ScheduledTask -TaskName $task).State.ToString()
        } -ArgumentList $taskName
    } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
    if ($state -eq 'Running') {
        throw "Golden renderer task timed out after $TimeoutSeconds seconds."
    }

    $taskResult = [int](Invoke-Command -Session $session -ScriptBlock {
        param($task)
        (Get-ScheduledTask -TaskName $task | Get-ScheduledTaskInfo).LastTaskResult
    } -ArgumentList $taskName)
    Copy-Item -FromSession $session -LiteralPath (Join-Path $guestRoot 'Results') `
        -Destination $artifacts -Recurse -Force
    $guestTestResults = Join-Path $guestRoot 'Source\mes\ingest\csharp\TestResults\watch-ui'
    if (Invoke-Command -Session $session -ScriptBlock {
            param($path) Test-Path -LiteralPath $path
        } -ArgumentList $guestTestResults) {
        Copy-Item -FromSession $session -LiteralPath $guestTestResults `
            -Destination (Join-Path $artifacts 'watch-ui') -Recurse -Force
    }

    if ($taskResult -ne 0) {
        throw "Golden renderer validation failed with scheduled-task result $taskResult."
    }
    [ordered]@{
        Status = 'PASSED'
        ScheduledTaskResult = $taskResult
        CompletedAt = [DateTimeOffset]::Now.ToString('O')
        GuestRunDirectory = $guestRoot
        ArtifactsDirectory = $artifacts
    } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $artifacts 'orchestration-result.json') -Encoding utf8
    Write-Output "GOLDEN_RENDERER_VALIDATION_PASSED: suite=$Suite artifacts=$artifacts"
}
finally {
    if ($null -ne $session) {
        $cleanup = Invoke-Command -Session $session -ScriptBlock {
            param($task)
            $scheduled = Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
            if ($null -ne $scheduled) {
                if ($scheduled.State -eq 'Running') {
                    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
                }
                Unregister-ScheduledTask -TaskName $task -Confirm:$false
            }
            Get-Process -Name 'MesIngest.Host', 'MesIngest.Watch', 'testhost', 'vstest.console' `
                -ErrorAction SilentlyContinue | Stop-Process -Force
            [pscustomobject]@{
                TaskPresent = $null -ne (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue)
                ResidualProcesses = @(Get-Process -Name `
                    'MesIngest.Host', 'MesIngest.Watch', 'testhost', 'vstest.console' `
                    -ErrorAction SilentlyContinue).Count
            }
        } -ArgumentList $taskName -ErrorAction SilentlyContinue
        Remove-PSSession $session
        if ($null -ne $cleanup) {
            [ordered]@{
                CompletedAt = [DateTimeOffset]::Now.ToString('O')
                TaskPresent = [bool]$cleanup.TaskPresent
                ResidualProcesses = [int]$cleanup.ResidualProcesses
            } | ConvertTo-Json |
                Set-Content -LiteralPath (Join-Path $artifacts 'cleanup.json') -Encoding utf8
        }
    }
    if (Test-Path -LiteralPath $tempRoot) {
        $resolvedTemp = (Resolve-Path -LiteralPath $tempRoot).Path
        $resolvedBase = (Resolve-Path -LiteralPath $tempBase).Path
        if (-not $resolvedTemp.StartsWith($resolvedBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unexpected temporary directory: $resolvedTemp"
        }
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
