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
    等锁的秒数。默认 0——拿不到就立刻以 3 退出，与 `Invoke-WatchUiTests.ps1` 一直以来的
    `WATCH_UI_SERIALIZATION_BUSY` 契约一致。本仓四个 workflow 已经被 `concurrency` 串起来了，所以
    在本仓内这把锁只会在「有人正手动跑桌面测试」时被占——那种时候立刻报错比默默等着好。

    真正需要排队的是**别的仓库**加进来的时候：CI 作业不该因为撞上另一个仓的调度就变红，那一天给它传
    一个真的超时。

.PARAMETER NameOnly
    只返回互斥体的名字，不运行任何东西。给 `Invoke-WatchUiTests.ps1` 用——它有自己的获取逻辑（保护
    的是直接手动调用这条路），但名字必须与这里同一个，所以不能各写一份字面量。

.EXAMPLE
    ./Invoke-WithDesktopLock.ps1 -Command { dotnet test MesIngest.Tests --filter $filter }

.NOTES
    **已知边界：跨账户还没打通。**目前所有持锁者都跑在交互式 runner 的同一个账户下，所以默认 DACL
    够用。哪天有个服务账户（session 0）的作业也要持这把锁，就需要给互斥体显式设一个允许两个账户的
    `MutexSecurity`，否则第二个账户会在 OpenExisting 上吃 UnauthorizedAccessException。到那时再加，
    不要提前猜 ACL 该长什么样。
#>
[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')]
    [scriptblock]$Command,

    [Parameter(ParameterSetName = 'Run')]
    [int]$TimeoutSeconds = 0,

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

$mutex = [Threading.Mutex]::new($false, $DesktopLockName)
$owned = $false
try {
    try {
        $owned = $mutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSeconds))
    } catch [Threading.AbandonedMutexException] {
        # 持有者死了没释放，**而且当时有人正在等**——等待者自己持着句柄，对象因此没有随持有者一起消失，
        # 于是它被标记为「已弃」并把等待者唤醒。**等待其实成功了，锁现在是我们的**：把这个异常当失败会
        # 让一次被杀的作业把锁毒化，那正是这里要避免的。
        #
        # 用 `-TimeoutSeconds 0`（本仓当前的用法）走不到这条路：没有人在等，对象就直接不存在了，
        # `WaitOne` 返回 `$true`。它是给跨仓库真排队那一天准备的——那时才会有等待者。**因此这四行目前
        # 没有测试覆盖**，要覆盖它得同时起一个持有者和一个阻塞的等待者再杀掉前者。
        Write-Warning "DESKTOP_LOCK_ABANDONED: 上一个持有者未释放就退出了，锁已归本进程。"
        $owned = $true
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
