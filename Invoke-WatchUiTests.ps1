[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [ValidateSet("watch-vm-tests", "watch-xaml-visual")]
    [string]$Suite = "watch-vm-tests"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Stop-WatchUiEnvironment {
    param([Parameter(Mandatory = $true)][string]$Reason)

    [Console]::Error.WriteLine("WATCH_UI_ENVIRONMENT_UNAVAILABLE: $Reason")
    exit 2
}

if (-not [Environment]::UserInteractive) {
    Stop-WatchUiEnvironment "The current Windows session is not interactive. Sign in to a desktop session and retry."
}

$currentProcess = [System.Diagnostics.Process]::GetCurrentProcess()
$sessionName = [Environment]::GetEnvironmentVariable("SESSIONNAME")
if ([string]::IsNullOrWhiteSpace($sessionName) -or $sessionName -eq "Services") {
    Stop-WatchUiEnvironment "No logged-in desktop session is associated with the test process."
}

$shellInSession = Get-Process -Name explorer -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $currentProcess.SessionId } |
    Select-Object -First 1
if ($null -eq $shellInSession) {
    Stop-WatchUiEnvironment "Windows Explorer is not running in session $($currentProcess.SessionId)."
}

if (-not ("WatchUiDesktop.NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace WatchUiDesktop
{
    public static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();
    }
}
"@
}

$desktop = [WatchUiDesktop.NativeMethods]::OpenInputDesktop(0, $false, 0x0100)
if ($desktop -eq [IntPtr]::Zero) {
    $lastError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Stop-WatchUiEnvironment "The interactive input desktop cannot be opened (Win32 error $lastError)."
}

[void][WatchUiDesktop.NativeMethods]::CloseDesktop($desktop)

if ($Suite -eq "watch-xaml-visual") {
    $differences = [System.Collections.Generic.List[string]]::new()
    $desktopWidth = [WatchUiDesktop.NativeMethods]::GetSystemMetrics(0)
    $desktopHeight = [WatchUiDesktop.NativeMethods]::GetSystemMetrics(1)
    if ($desktopWidth -ne 1920 -or $desktopHeight -ne 1080) {
        $differences.Add("expected desktop=1920x1080; actual=${desktopWidth}x${desktopHeight}")
    }

    $dpi = [WatchUiDesktop.NativeMethods]::GetDpiForSystem()
    if ($dpi -ne 96) {
        $differences.Add("expected DPI=96 (100%); actual=$dpi")
    }

    $theme = Get-ItemPropertyValue `
        -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" `
        -Name "AppsUseLightTheme" `
        -ErrorAction SilentlyContinue
    if ($theme -ne 1) {
        $actualTheme = if ($null -eq $theme) { "unknown" } else { $theme }
        $differences.Add("expected Windows apps light theme; actual=$actualTheme")
    }

    $culture = [Globalization.CultureInfo]::CurrentCulture.Name
    if ($culture -ne "zh-CN") {
        $differences.Add("expected culture=zh-CN; actual=$culture")
    }

    $uiCulture = [Globalization.CultureInfo]::CurrentUICulture.Name
    if ($uiCulture -ne "zh-CN") {
        $differences.Add("expected UI culture=zh-CN; actual=$uiCulture")
    }

    $requiredFontFiles = @{
        "Microsoft YaHei UI" = Join-Path $env:WINDIR "Fonts\msyh.ttc"
        "Consolas" = Join-Path $env:WINDIR "Fonts\consola.ttf"
    }
    foreach ($font in $requiredFontFiles.GetEnumerator()) {
        if (-not (Test-Path -LiteralPath $font.Value -PathType Leaf)) {
            $differences.Add("expected installed font=$($font.Key); actual=missing")
        }
    }

    $renderingMode = [Environment]::GetEnvironmentVariable("MesIngestWatch__RenderingMode")
    if (-not [string]::IsNullOrWhiteSpace($renderingMode) -and $renderingMode -ne "SoftwareOnly") {
        $differences.Add("expected rendering mode=SoftwareOnly; actual=$renderingMode")
    }

    if ($differences.Count -gt 0) {
        [Console]::Error.WriteLine("WATCH_XAML_VISUAL_ENVIRONMENT_UNAVAILABLE:")
        foreach ($difference in $differences) {
            [Console]::Error.WriteLine("- $difference")
        }
        exit 2
    }
}

$project = Join-Path $PSScriptRoot "MesIngest.Watch.UiTests\MesIngest.Watch.UiTests.csproj"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = "1"

$traitArgument = if ($Suite -eq "watch-xaml-visual") {
    @("-trait", "Category=watch-xaml-visual")
} else {
    @("-trait-", "Category=watch-xaml-visual")
}

Write-Host "WATCH_UI_ENVIRONMENT_OK: session=$($currentProcess.SessionId) name=$sessionName; suite=$Suite; running serial xUnit v3 UI tests."
& dotnet run --project $project --configuration $Configuration -- @traitArgument -parallel none
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
