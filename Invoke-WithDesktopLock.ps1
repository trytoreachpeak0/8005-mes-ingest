#Requires -Version 7

<#
.SYNOPSIS
    在机器级的「交互式桌面」互斥体下运行一段命令。

.DESCRIPTION
    这台客户机上只有一个交互式桌面，而 win11-01 同时给 riot-sdk、control server 和 protocol 挂着
    runner。**GitHub 的 `concurrency` 只在单个仓库内生效**，所以本仓的 `concurrency: group: win11-01`
    能把本仓四个 workflow 串起来，却对别的仓库一无所知。跨仓库的桌面独占只能靠一个机器级对象。

    这个脚本就是那个对象的唯一定义处。名字本身才是契约——任何仓库、任何脚本，只要在动这台机器的
    交互式桌面（起 WPF 窗口、抓像素、驱动 UI Automation），就必须先拿到同一个名字的互斥体。

    **第二个仓已经在了（2026-09-08）。**`8005-agv-control-server` 有自己的一份实现
    （`scripts/DesktopLock.psm1`，模块而非包装脚本，因为它的四个持锁者都在脚本内部取锁），名字与这里
    逐字符相同。两边各有一条 `Test-DesktopLockQueueing.ps1` 断言这个字面量，所以名字漂移会被抓到而不
    是变成两把互不相识的锁——那种失败没有任何征兆，只表现为两套桌面测试偶尔一起跑。

    **为什么是命名内核互斥体而不是锁文件。**内核对象随最后一个句柄消失。所以作业被杀、runner 会话
    重启、进程崩溃之后，如果当时没有别人持句柄，这个对象直接就不存在了，下一个来的进程新建一个同名
    的、未被持有的互斥体——**残锁在物理上不可能存在**。实测确认过：杀掉唯一的持有者之后，下一次
    `WaitOne(0)` 直接返回 `$true`，连 `AbandonedMutexException` 都不会抛。

    锁文件做不到这一点：文件会留在盘上，于是需要一套「这把锁是不是过期了」的启发式判断，而那套判断
    出错的方式正好是最坏的那种——两套桌面测试同时跑。

    `Global\` 前缀是必需的：服务模式 runner 在 session 0，交互式 runner 在 session 1，没有 `Global\`
    的名字只在同一个会话里可见。

.PARAMETER Command
    要在锁下运行的命令。它的退出码会被原样传出去。

.PARAMETER TimeoutSeconds
    等锁的秒数，默认 1800。超时后仍以 3 退出，与 `Invoke-WatchUiTests.ps1` 一直以来的
    `WATCH_UI_SERIALIZATION_BUSY` 契约一致——**改的是等多久才失败，不是失败长什么样**。

    **2026-09-08 之前默认是 0**，拿不到就立刻失败。那在当时是对的：本仓是唯一的持锁者，本仓四个
    workflow 又已被 `concurrency: group: win11-01` 串起来，于是撞锁只可能意味着「有人正手动跑桌面
    测试」，立刻报错比默默等着好。

    `8005-agv-control-server` 加进来之后这个前提没了。它的 L2 真装置场景与分阶段 G3 会在同一台机器上
    起 WPF 窗口，而两个仓的调度互相看不见。**CI 作业该排队，不该因为撞上另一个仓的调度就变红。**

    1800 秒的来历：单次持锁的实测上界约 8 分钟（`watch-window-visual` 一次迭代 ~7.3 分钟，
    `desktop-tests` 那 163 个测试几分钟，对面仓的 L2 真装置场景分钟级），30 分钟留了三倍余量，同时仍
    是有限值——真出异常时以 `DESKTOP_LOCK_BUSY` 说明原因，比挂到 job 的 `timeout-minutes` 上、只留一
    行 "The job running on runner ... has exceeded the maximum execution time" 强得多。

    **调用方的 `timeout-minutes` 必须容得下「等待 ＋ 自身」**，否则排队照样变红，只是换个红法。本仓两
    个桌面 workflow 已按此调过。

    传 0 仍然是快速失败。`Test-DesktopLockQueueing.ps1` 用它构造对照的那一半。

.PARAMETER NameOnly
    只返回互斥体的名字，不运行任何东西。给 `Invoke-WatchUiTests.ps1` 用——它有自己的获取逻辑（保护
    的是直接手动调用这条路），但名字必须与这里同一个，所以不能各写一份字面量。

.EXAMPLE
    ./Invoke-WithDesktopLock.ps1 -Command { dotnet test MesIngest.Tests --filter $filter }

.NOTES
    **已知边界：跨账户还没打通。**目前所有持锁者都跑在交互式 runner 的同一个账户下，所以默认 DACL
    够用。`8005-agv-control-server` 的持锁者也在那个账户下——它的四个入口都是人在交互式会话里手动跑
    的，没有一个由 session 0 的服务 runner 触发——所以第二个仓加进来没有改变这条边界。

    哪天有个服务账户（session 0）的作业也要持这把锁，就需要给互斥体显式设一个允许两个账户的
    `MutexSecurity`，否则第二个账户会在 OpenExisting 上吃 UnauthorizedAccessException。到那时再加，
    不要提前猜 ACL 该长什么样。
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')]
    [scriptblock]$Command,

    [Parameter(ParameterSetName = 'Run')]
    [ValidateRange(0, 86400)]
    [int]$TimeoutSeconds = 1800,

    [Parameter(Mandatory, ParameterSetName = 'Name')]
    [switch]$NameOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 这里是这个名字的唯一定义处。改它等于改契约，所有持锁者要一起改。
$DesktopLockName = 'Global\W2G-InteractiveDesktop'

if ($NameOnly) {
    return $DesktopLockName
}

function Request-DesktopLock {
    param(
        [Parameter(Mandatory)][Threading.Mutex]$Mutex,
        [Parameter(Mandatory)][int]$Seconds
    )

    try {
        return $Mutex.WaitOne([TimeSpan]::FromSeconds($Seconds))
    } catch [Threading.AbandonedMutexException] {
        # 持有者死了没释放，**而且当时有人正在等**——等待者自己持着句柄，对象因此没有随持有者一起消失，
        # 于是它被标记为「已弃」并把等待者唤醒。**等待其实成功了，锁现在是我们的**：把这个异常当失败会
        # 让一次被杀的作业把锁毒化，那正是这里要避免的。
        #
        # 这条路以前走不到：`-TimeoutSeconds 0` 时没有人在等，对象就直接不存在了，`WaitOne` 返回
        # `$true`。默认改成真等待之后它才第一次成为可达代码，`Test-DesktopLockQueueing.ps1` 的第二个
        # 用例就是为它写的——起一个持有者、让等待者阻塞上去、再杀掉持有者。
        Write-Warning "DESKTOP_LOCK_ABANDONED: 上一个持有者未释放就退出了，锁已归本进程。"
        return $true
    }
}

$mutex = [Threading.Mutex]::new($false, $DesktopLockName)
$owned = $false
try {
    # 先零等待试一次，只为了知道要不要说话。**一个等了二十分钟的 job，日志里必须写着它在等什么**，
    # 否则现场看到的就是一段无输出的空白，与卡死无法区分——那正是跨仓排队最容易被误诊成故障的地方。
    $owned = Request-DesktopLock -Mutex $mutex -Seconds 0
    if (-not $owned -and $TimeoutSeconds -gt 0) {
        Write-Host ("DESKTOP_LOCK_WAITING: 另一个进程正占用这台机器的交互式桌面（$DesktopLockName），" +
            "最多等待 ${TimeoutSeconds}s。")
        $owned = Request-DesktopLock -Mutex $mutex -Seconds $TimeoutSeconds
        if ($owned) {
            Write-Host 'DESKTOP_LOCK_ACQUIRED: 排队结束，交互式桌面已归本进程。'
        }
    }

    if (-not $owned) {
        [Console]::Error.WriteLine(
            "DESKTOP_LOCK_BUSY: 另一个进程正占用这台机器的交互式桌面（$DesktopLockName），" +
            "等待 ${TimeoutSeconds}s 未获得。")
        exit 3
    }

    # 先清掉调用方留下的 $LASTEXITCODE。它是会话级的，而一段只跑 cmdlet 的命令根本不会设置它——不清
    # 的话，我们会把**上一条无关命令**的退出码当成自己的传出去。CI 里每一步都是新进程，所以这个坑在
    # 那里看不出来；同一个会话里连调两次就会中招，实测第二次拿着前一次的 3 退出，而它其实成功了。
    $global:LASTEXITCODE = $null
    & $Command
    exit $(if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE })
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
