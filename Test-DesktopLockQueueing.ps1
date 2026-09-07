#Requires -Version 7

<#
.SYNOPSIS
    自检：证明 `Invoke-WithDesktopLock.ps1` 的第二个持有者会排队等待，而不是立刻失败。

.DESCRIPTION
    这把锁的默认行为在 2026-09-08 从「拿不到就以 3 退出」改成「等 1800 秒」，因为
    `8005-agv-control-server` 加进来成了第二个持锁者，而两个仓的 CI 调度互相看不见。这个脚本是那次
    改动的证据：**它真的起两个进程去抢同一把锁**，而不是断言一段常量。

    五条断言，每条各自说明它守住的是什么：

    1. **名字契约** —— `-NameOnly` 必须返回 `Global\W2G-InteractiveDesktop` 这个字面量。名字才是跨仓
       契约，而两个仓是独立克隆、没有共享包，所以唯一能拦住名字漂移的就是两边各有一条这样的断言。
       `8005-agv-control-server/scripts/Test-DesktopLockQueueing.ps1` 是它的对面一半。
    2. **快速失败仍可用** —— `-TimeoutSeconds 0` 撞上占用者仍是 exit 3。改的是默认值，不是能力。
    3. **排队** —— 占用者持锁不放，等待者带超时进来阻塞；确认它真的阻塞住之后再放行占用者，等待者必须
       **等到**并以 0 退出。断言带一条时间下界，否则一个根本没拿到锁却返回 0 的实现也能骗过它。
    4. **排队要出声** —— 日志里必须同时有 `DESKTOP_LOCK_WAITING` 与 `DESKTOP_LOCK_ACQUIRED`。一个静默
       等了二十分钟的 CI 作业与一个卡死的作业在日志上无法区分，那是排队最容易被误诊的地方。
    5. **弃锁** —— 占用者被杀时等待者正阻塞在 `WaitOne` 上，于是对象被标记为「已弃」。等待者必须拿到
       锁并继续，而不是把 `AbandonedMutexException` 当失败。**这条路在默认改成真等待之前是不可达
       的**，helper 里那段处理因此一直没有覆盖；现在有了。

    **每一步的时机都由信号文件决定，没有一处靠 `Start-Sleep` 猜。**占用者持锁到主进程放行为止，主进程
    则等到等待者的日志里真的出现 `DESKTOP_LOCK_WAITING` 才动手。写成固定睡眠的版本会在 pwsh 冷启动慢
    的机器上假红——那种偶发红比没有自检更糟。

.PARAMETER QueueSeconds
    用例 3 里让等待者实打实排队的秒数。默认 3。这是主进程确认等待者已阻塞之后才开始计的，所以它是真
    实等待时间的下界，与 pwsh 的启动耗时无关。

.PARAMETER WaitTimeoutSeconds
    等待者的等锁超时。默认 60，只是个上界：正常情况下等待者在占用者放手的瞬间就返回。

.EXAMPLE
    ./Test-DesktopLockQueueing.ps1

.NOTES
    **它会真的短暂持有 `Global\W2G-InteractiveDesktop`。**用的是真锁名而不是一个隔离的测试名字，因为
    自检要证的正是那把锁的行为——换个名字测出来的是一段与生产无关的代码。代价是：在 win11-01 上跑它的
    那几秒里，桌面作业会排队。这不会弄坏任何东西（排队正是它要证明的），但别在一次三连跑基线渲染的中
    途手动跑它。

    退出码即契约：0 全过，1 有断言失败。
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$QueueSeconds = 3,

    [ValidateRange(5, 600)]
    [int]$WaitTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$helper = Join-Path $PSScriptRoot 'Invoke-WithDesktopLock.ps1'
if (-not (Test-Path -LiteralPath $helper -PathType Leaf)) {
    throw "找不到被测脚本：$helper"
}

$stage = Join-Path ([IO.Path]::GetTempPath()) ("desktop-lock-selftest-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $stage

# 占用者与等待者都写成临时脚本文件再用 `-File` 调起，而不是 `-Command` 加一串内联代码：内联那条路要把
# 含大括号和引号的脚本文本穿过 Start-Process 的参数引用规则，出错方式是「参数被截断，进程静默地做了别
# 的事」，而自检恰恰不能有这种失败模式。
$holderScript = Join-Path $stage 'holder.ps1'
@'
#Requires -Version 7
param([Parameter(Mandatory)][string]$LockName,
      [Parameter(Mandatory)][string]$ReadyFile,
      [Parameter(Mandatory)][string]$ReleaseFile,
      [Parameter(Mandatory)][int]$MaxHoldSeconds)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 占用者故意不走被测 helper：它只需要是「另一个真的持着这把锁的进程」。用 helper 会让用例的成立依赖于
# 被测代码本身正确，那就测不出东西了。
$mutex = [Threading.Mutex]::new($false, $LockName)
if (-not $mutex.WaitOne([TimeSpan]::FromSeconds(30))) {
    throw "占用者自己没拿到锁，机器上有别的持有者：$LockName"
}
try {
    Set-Content -LiteralPath $ReadyFile -Value 'held' -Encoding utf8NoBOM
    # 持到主进程放行为止。MaxHoldSeconds 只是兜底：主进程若自己炸了，这个进程不该永远占着机器的桌面锁。
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($MaxHoldSeconds)
    while (-not (Test-Path -LiteralPath $ReleaseFile) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
'@ | Set-Content -LiteralPath $holderScript -Encoding utf8NoBOM

$waiterScript = Join-Path $stage 'waiter.ps1'
@'
#Requires -Version 7
param([Parameter(Mandatory)][string]$Helper,
      [Parameter(Mandatory)][int]$TimeoutSeconds,
      [Parameter(Mandatory)][string]$ResultFile)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$logFile = $ResultFile + '.log'
$startedAt = [DateTimeOffset]::UtcNow
# 逐行落盘，不是 `| Out-File`。后者要等整条管道结束才写，于是「等待者已经阻塞在锁上」这个信号会与
# 「等待者已经退出」同时到达——主进程就再也无法在它阻塞期间动手，用例 3 和 5 都建立不起来。
& $Helper -TimeoutSeconds $TimeoutSeconds -Command { 'acquired' } *>&1 |
    ForEach-Object { $_.ToString() | Out-File -LiteralPath $logFile -Append -Encoding utf8 }
$exitCode = $LASTEXITCODE
[ordered]@{
    exitCode = $exitCode
    elapsedSeconds = ([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds
} | ConvertTo-Json | Set-Content -LiteralPath $ResultFile -Encoding utf8NoBOM
'@ | Set-Content -LiteralPath $waiterScript -Encoding utf8NoBOM

$failures = @()
function Assert-Case {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][bool]$Condition,
          [Parameter(Mandatory)][string]$Detail)

    if ($Condition) {
        Write-Host "PASS  $Name — $Detail"
    } else {
        Write-Host "FAIL  $Name — $Detail"
        $script:failures += $Name
    }
}

function Start-Holder {
    param([Parameter(Mandatory)][string]$LockName,
          [Parameter(Mandatory)][string]$ReadyFile,
          [Parameter(Mandatory)][string]$ReleaseFile)

    $process = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-File', $holderScript,
        '-LockName', $LockName, '-ReadyFile', $ReadyFile,
        '-ReleaseFile', $ReleaseFile, '-MaxHoldSeconds', 300)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $ReadyFile) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "占用者提前退出，exitCode=$($process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $ReadyFile)) { throw '占用者没能在 60 秒内拿到锁' }
    return $process
}

function Start-Waiter {
    param([Parameter(Mandatory)][int]$TimeoutSeconds,
          [Parameter(Mandatory)][string]$ResultFile)

    return Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-File', $waiterScript,
        '-Helper', $helper, '-TimeoutSeconds', $TimeoutSeconds, '-ResultFile', $ResultFile)
}

function Wait-WaiterBlocked {
    param([Parameter(Mandatory)][string]$ResultFile,
          [Parameter(Mandatory)][Diagnostics.Process]$Process)

    $logPath = $ResultFile + '.log'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $logPath) {
            $log = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
            if ($log -match 'DESKTOP_LOCK_WAITING') { return }
        }
        if ($Process.HasExited) { throw "等待者在进入等待前就退出了，exitCode=$($Process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    throw '等待者没有在 60 秒内报出 DESKTOP_LOCK_WAITING'
}

function Read-WaiterResult {
    param([Parameter(Mandatory)][string]$ResultFile)

    if (-not (Test-Path -LiteralPath $ResultFile)) { throw "等待者没有写结果：$ResultFile" }
    $result = Get-Content -LiteralPath $ResultFile -Raw | ConvertFrom-Json
    $logPath = $ResultFile + '.log'
    $log = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
    return [pscustomobject]@{
        ExitCode = [int]$result.exitCode
        ElapsedSeconds = [double]$result.elapsedSeconds
        Log = $log
    }
}

$holders = @()
try {
    # 1. 名字契约。
    $lockName = & $helper -NameOnly
    Assert-Case -Name 'lock-name-is-the-contract' `
        -Condition ($lockName -eq 'Global\W2G-InteractiveDesktop') `
        -Detail "-NameOnly 返回 '$lockName'"

    # 2. 快速失败仍然可用：占用者在场时 -TimeoutSeconds 0 立刻以 3 退出。
    $release2 = Join-Path $stage 'release-2'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-2') -ReleaseFile $release2
    $holders += $holder
    $resultFile = Join-Path $stage 'result-2.json'
    $waiter = Start-Waiter -TimeoutSeconds 0 -ResultFile $resultFile
    $waiter.WaitForExit()
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'zero-timeout-still-fails-fast' `
        -Condition ($result.ExitCode -eq 3 -and $result.ElapsedSeconds -lt 10) `
        -Detail ("exitCode={0}，耗时 {1:N1}s（占用者仍持有）" -f $result.ExitCode, $result.ElapsedSeconds)
    Set-Content -LiteralPath $release2 -Value 'go' -Encoding utf8NoBOM
    $holder.WaitForExit(60000) | Out-Null

    # 3、4. 排队：等待者先阻塞，主进程确认它阻塞住了再计时放行，它必须等到而不是失败。
    $release3 = Join-Path $stage 'release-3'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-3') -ReleaseFile $release3
    $holders += $holder
    $resultFile = Join-Path $stage 'result-3.json'
    $waiter = Start-Waiter -TimeoutSeconds $WaitTimeoutSeconds -ResultFile $resultFile
    Wait-WaiterBlocked -ResultFile $resultFile -Process $waiter
    Start-Sleep -Seconds $QueueSeconds
    Set-Content -LiteralPath $release3 -Value 'go' -Encoding utf8NoBOM
    $waiter.WaitForExit()
    $holder.WaitForExit(60000) | Out-Null
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'second-holder-queues-instead-of-failing' `
        -Condition ($result.ExitCode -eq 0 -and $result.ElapsedSeconds -ge $QueueSeconds) `
        -Detail ("exitCode={0}，等了 {1:N1}s（下界 {2}s）" -f
            $result.ExitCode, $result.ElapsedSeconds, $QueueSeconds)
    Assert-Case -Name 'queued-run-says-it-is-queueing' `
        -Condition ($result.Log -match 'DESKTOP_LOCK_WAITING' -and $result.Log -match 'DESKTOP_LOCK_ACQUIRED') `
        -Detail '日志里同时有 DESKTOP_LOCK_WAITING 与 DESKTOP_LOCK_ACQUIRED'

    # 5. 弃锁：占用者被杀时等待者正阻塞着，锁被标记为已弃，等待者必须接手而不是失败。
    $release5 = Join-Path $stage 'release-5'
    $holder = Start-Holder -LockName $lockName -ReadyFile (Join-Path $stage 'ready-5') -ReleaseFile $release5
    $holders += $holder
    $resultFile = Join-Path $stage 'result-5.json'
    $waiter = Start-Waiter -TimeoutSeconds $WaitTimeoutSeconds -ResultFile $resultFile
    Wait-WaiterBlocked -ResultFile $resultFile -Process $waiter
    Stop-Process -Id $holder.Id -Force
    $holder.WaitForExit(60000) | Out-Null
    $waiter.WaitForExit()
    $result = Read-WaiterResult -ResultFile $resultFile
    Assert-Case -Name 'abandoned-lock-is-inherited-not-poisoned' `
        -Condition ($result.ExitCode -eq 0) `
        -Detail ("占用者被杀后等待者 exitCode={0}，耗时 {1:N1}s" -f $result.ExitCode, $result.ElapsedSeconds)
    Assert-Case -Name 'abandoned-lock-is-reported' `
        -Condition ($result.Log -match 'DESKTOP_LOCK_ABANDONED') `
        -Detail '日志里有 DESKTOP_LOCK_ABANDONED 警告'
} finally {
    # 自检若中途抛出，占用者会一直持着这台机器的桌面锁到它自己的 MaxHoldSeconds 兜底为止。那是五分钟的
    # 桌面停摆，所以这里主动收掉。
    foreach ($process in $holders) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("DESKTOP_LOCK_SELFTEST_FAILED: $($failures -join ', ')")
    exit 1
}

Write-Host 'DESKTOP_LOCK_SELFTEST_PASSED: 名字契约、快速失败、排队、排队可见、弃锁接手五项均成立。'
exit 0
