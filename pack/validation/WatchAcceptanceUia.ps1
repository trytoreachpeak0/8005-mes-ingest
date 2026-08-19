#Requires -Version 5.1
<#
.SYNOPSIS
  UI Automation helpers for the ticket 26 Watch verification on the plant desktop.

.DESCRIPTION
  Dot-source this file; it defines functions and runs nothing. It drives the packaged
  Watch against a live Host through UI Automation and records what was observed.

  This is field verification of the final packaging against live Host data. It is not a
  visual gate: no pixel candidate, stability run, baseline promotion or DPI clone from
  ticket 23 is repeated or re-approved here. Window captures are written beside the run
  for the operator; they carry customer rows, so only their hashes are published.
#>

Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

if (-not ('MesIngest.Acceptance.NativeWindow' -as [type])) {
    Add-Type -Namespace 'MesIngest.Acceptance' -Name 'NativeWindow' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hWnd);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);

public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@
}

# The six workspace pages, plus settings and the compact Host state item, named as the
# operator sees them. The page element proves the page actually rendered.
$script:WatchPageWalk = @(
    [ordered]@{ Nav = '概览导航'; Page = 'OverviewPage'; Label = '概览' }
    [ordered]@{ Nav = '需求系列导航'; Page = 'DemandSeriesPage'; Label = '需求系列' }
    [ordered]@{ Nav = '资格审计导航'; Page = 'ReadabilityAuditPage'; Label = '资格审计' }
    [ordered]@{ Nav = '错误检索导航'; Page = 'ErrorSearchPage'; Label = '错误检索' }
    [ordered]@{ Nav = 'AREA 筛选导航'; Page = 'AreaFilterPage'; Label = 'AREA 筛选' }
    [ordered]@{ Nav = '接入告警导航'; Page = 'CurrentAttentionPage'; Label = '接入告警' }
    [ordered]@{ Nav = '设置导航'; Page = 'SettingsPage'; Label = '设置' }
    [ordered]@{ Nav = 'Host 状态与连接设置'; Page = 'SettingsPage'; Label = 'Host 状态' }
)

function Wait-WatchMainWindow {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process] $Process,
        [ValidateRange(5, 600)][int] $TimeoutSeconds = 90
    )

    try {
        $reachedIdle = $Process.WaitForInputIdle([int]([TimeSpan]::FromSeconds($TimeoutSeconds).TotalMilliseconds))
    } catch [InvalidOperationException] {
        # WaitForInputIdle throws rather than returning once the process is gone, which
        # would hide the real startup failure behind a Win32 message.
        $Process.WaitForExit(5000) | Out-Null
        throw "The packaged Watch exited during startup with code $($Process.ExitCode)."
    }
    if (-not $reachedIdle) {
        throw "The packaged Watch did not reach an idle input state within $TimeoutSeconds seconds."
    }

    # Input idle means the message pump started, which happens before WPF has shown the
    # window, so poll for the real window instead of sampling once.
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "The packaged Watch exited before showing a main window (code $($Process.ExitCode))."
        }
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $element = [Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
            if ($null -ne $element) { return $element }
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "The packaged Watch never showed a main window within $TimeoutSeconds seconds."
}

function Find-WatchElement {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][Windows.Automation.AutomationElement] $Root,
        [string] $AutomationId,
        [string] $Name,
        [ValidateRange(0, 600)][int] $TimeoutSeconds = 20
    )

    if ([string]::IsNullOrWhiteSpace($AutomationId) -and [string]::IsNullOrWhiteSpace($Name)) {
        throw 'Find-WatchElement needs an AutomationId or a Name.'
    }
    $condition = if (-not [string]::IsNullOrWhiteSpace($AutomationId)) {
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    } else {
        [Windows.Automation.PropertyCondition]::new(
            [Windows.Automation.AutomationElement]::NameProperty, $Name)
    }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $null
}

function Wait-WatchElementVisible {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][Windows.Automation.AutomationElement] $Root,
        [Parameter(Mandatory = $true)][string] $AutomationId,
        [ValidateRange(1, 600)][int] $TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = Find-WatchElement -Root $Root -AutomationId $AutomationId -TimeoutSeconds 1
        if ($null -ne $element -and -not $element.Current.IsOffscreen) {
            return $element
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $null
}

function Invoke-WatchElement {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][Windows.Automation.AutomationElement] $Element)

    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) {
        $pattern.Invoke()
        return $true
    }
    if ($Element.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) {
        $pattern.Select()
        return $true
    }
    return $false
}

function Get-WatchElementText {
    [CmdletBinding()]
    param([AllowNull()][Windows.Automation.AutomationElement] $Element)

    if ($null -eq $Element) { return '' }
    $pattern = $null
    if ($Element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref] $pattern)) {
        $value = [string]$pattern.Current.Value
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value }
    }
    return [string]$Element.Current.Name
}

function Save-WatchWindowCapture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][Windows.Automation.AutomationElement] $Window,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) { return $false }
    [void][MesIngest.Acceptance.NativeWindow]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 400

    $rect = New-Object MesIngest.Acceptance.NativeWindow+RECT
    if (-not [MesIngest.Acceptance.NativeWindow]::GetWindowRect($handle, [ref] $rect)) { return $false }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { return $false }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        } finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
    return $true
}

<#
.SYNOPSIS
  Walk the packaged Watch across its pages against live Host data.

.DESCRIPTION
  Records what was actually observed rather than asserting a fixed plant state: pages
  shown, the Host connection state, auto-refresh advancing the view, and whichever of
  paging, detail selection and cross-page drill the live data allowed. Anything the data
  did not allow is reported as not exercised, never as passed.
#>
function Invoke-WatchPageWalk {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][Windows.Automation.AutomationElement] $Window,
        [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
        [ValidateRange(5, 600)][int] $ObservationSeconds = 45
    )

    $pagesShown = New-Object System.Collections.ArrayList
    $missingPages = New-Object System.Collections.ArrayList
    $captures = New-Object System.Collections.ArrayList

    foreach ($step in $script:WatchPageWalk) {
        $navigation = Find-WatchElement -Root $Window -Name $step.Nav -TimeoutSeconds 20
        if ($null -eq $navigation) {
            [void]$missingPages.Add("$($step.Label) (navigation item not found)")
            continue
        }
        if (-not (Invoke-WatchElement -Element $navigation)) {
            [void]$missingPages.Add("$($step.Label) (navigation item is not invokable)")
            continue
        }
        $page = Wait-WatchElementVisible -Root $Window -AutomationId $step.Page -TimeoutSeconds 30
        if ($null -eq $page) {
            [void]$missingPages.Add("$($step.Label) ($($step.Page) never became visible)")
            continue
        }
        [void]$pagesShown.Add($step.Label)

        $capturePath = Join-Path $EvidenceDirectory ("page-{0:D2}-{1}.png" -f
            ($pagesShown.Count), $step.Page)
        if (Save-WatchWindowCapture -Window $Window -Path $capturePath) {
            [void]$captures.Add((Split-Path -Leaf $capturePath))
        }
    }

    # Host connection state, as the operator reads it on the overview and in settings.
    $hostStatusText = ''
    $overviewNavigation = Find-WatchElement -Root $Window -Name '概览导航' -TimeoutSeconds 10
    if ($null -ne $overviewNavigation) {
        [void](Invoke-WatchElement -Element $overviewNavigation)
        [void](Wait-WatchElementVisible -Root $Window -AutomationId 'OverviewPage' -TimeoutSeconds 20)
        $hostStatusText = Get-WatchElementText -Element (
            Find-WatchElement -Root $Window -AutomationId 'OverviewHostStatusText' -TimeoutSeconds 10)
    }

    # Auto-refresh: the overview context line carries the client read time, so a changed
    # value is the Watch having refreshed itself against the live Host.
    $contextElement = Find-WatchElement -Root $Window -AutomationId 'OverviewContextText' -TimeoutSeconds 10
    $observedContexts = New-Object System.Collections.Generic.HashSet[string]
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($ObservationSeconds)
    do {
        $text = Get-WatchElementText -Element $contextElement
        if (-not [string]::IsNullOrWhiteSpace($text)) { [void]$observedContexts.Add($text) }
        Start-Sleep -Milliseconds 1000
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $autoRefreshObservations = [Math]::Max(0, $observedContexts.Count - 1)

    $interactions = New-Object System.Collections.ArrayList

    # Paging and detail selection on the Demand series page.
    $seriesNavigation = Find-WatchElement -Root $Window -Name '需求系列导航' -TimeoutSeconds 10
    if ($null -ne $seriesNavigation) {
        [void](Invoke-WatchElement -Element $seriesNavigation)
        [void](Wait-WatchElementVisible -Root $Window -AutomationId 'DemandSeriesPage' -TimeoutSeconds 30)

        $grid = Find-WatchElement -Root $Window -AutomationId 'DemandSeriesGrid' -TimeoutSeconds 20
        $rows = if ($null -eq $grid) {
            @()
        } else {
            @($grid.FindAll([Windows.Automation.TreeScope]::Children,
                [Windows.Automation.PropertyCondition]::new(
                    [Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [Windows.Automation.ControlType]::DataItem)))
        }
        if ($rows.Count -eq 0) {
            [void]$interactions.Add('detail selection: not exercised (the live snapshot had no Demand rows)')
        } else {
            if (Invoke-WatchElement -Element $rows[0]) {
                $detail = Wait-WatchElementVisible -Root $Window -AutomationId 'DemandSeriesEventGrid' -TimeoutSeconds 20
                if ($null -ne $detail) {
                    [void]$interactions.Add("detail selection: exercised on 1 of $($rows.Count) live rows")
                } else {
                    [void]$interactions.Add('detail selection: selected a live row but no detail grid appeared')
                }
            } else {
                [void]$interactions.Add('detail selection: the live row could not be selected')
            }
        }

        $nextButton = Find-WatchElement -Root $Window -AutomationId 'DemandSeriesNextButton' -TimeoutSeconds 10
        if ($null -eq $nextButton) {
            [void]$interactions.Add('paging: not exercised (no next-page control)')
        } elseif (-not $nextButton.Current.IsEnabled) {
            [void]$interactions.Add("paging: not exercised (the live snapshot fits one page of $($rows.Count) rows)")
        } else {
            $pageInput = Find-WatchElement -Root $Window -AutomationId 'DemandSeriesPageNumberInput' -TimeoutSeconds 10
            $before = Get-WatchElementText -Element $pageInput
            [void](Invoke-WatchElement -Element $nextButton)
            Start-Sleep -Milliseconds 1500
            $after = Get-WatchElementText -Element $pageInput
            if ($after -cne $before) {
                [void]$interactions.Add("paging: exercised on live data (page $before -> $after)")
            } else {
                [void]$interactions.Add("paging: the next-page control did not move the page (stayed at $before)")
            }
        }
    }

    # Cross-page drill: attention item to the error search page.
    $attentionNavigation = Find-WatchElement -Root $Window -Name '接入告警导航' -TimeoutSeconds 10
    if ($null -ne $attentionNavigation) {
        [void](Invoke-WatchElement -Element $attentionNavigation)
        [void](Wait-WatchElementVisible -Root $Window -AutomationId 'CurrentAttentionPage' -TimeoutSeconds 30)
        $drill = Find-WatchElement -Root $Window -AutomationId 'CurrentAttentionOpenErrorSearchButton' -TimeoutSeconds 10
        if ($null -eq $drill -or -not $drill.Current.IsEnabled) {
            [void]$interactions.Add('cross-page drill: not exercised (no attention item was available to drill from)')
        } else {
            [void](Invoke-WatchElement -Element $drill)
            $errorPage = Wait-WatchElementVisible -Root $Window -AutomationId 'ErrorSearchPage' -TimeoutSeconds 30
            if ($null -ne $errorPage) {
                [void]$interactions.Add('cross-page drill: exercised from an attention item into error search')
            } else {
                [void]$interactions.Add('cross-page drill: the attention item did not open the error search page')
            }
        }
    }

    return [pscustomobject][ordered]@{
        PagesShown = @($pagesShown)
        MissingPages = @($missingPages)
        AllPagesShown = ($missingPages.Count -eq 0)
        HostStatusText = $hostStatusText
        HostConnected = ($hostStatusText -match '已连接')
        AutoRefreshObservations = $autoRefreshObservations
        AutoRefreshObserved = ($autoRefreshObservations -gt 0)
        InteractionSummary = (@($interactions) -join '; ')
        Captures = @($captures)
    }
}
