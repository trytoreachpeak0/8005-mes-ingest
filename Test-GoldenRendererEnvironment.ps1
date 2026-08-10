[CmdletBinding()]
param(
    [ValidateSet(96, 120, 144)]
    [int]$ExpectedDpi = 96,

    [ValidateRange(1, 16384)]
    [int]$ExpectedDesktopWidth = 1920,

    [ValidateRange(1, 16384)]
    [int]$ExpectedDesktopHeight = 1080,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not ('GoldenRenderer.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace GoldenRenderer
{
    public static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseDesktop(IntPtr desktop);
    }
}
'@
}

Add-Type -AssemblyName PresentationCore
$process = [Diagnostics.Process]::GetCurrentProcess()
$explorer = Get-Process explorer -ErrorAction SilentlyContinue |
    Where-Object SessionId -eq $process.SessionId |
    Select-Object -First 1
$inputDesktop = [GoldenRenderer.NativeMethods]::OpenInputDesktop(0, $false, 0x0100)
$hasInputDesktop = $inputDesktop -ne [IntPtr]::Zero
if ($hasInputDesktop) {
    [void][GoldenRenderer.NativeMethods]::CloseDesktop($inputDesktop)
}

$theme = Get-ItemProperty `
    -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' `
    -ErrorAction SilentlyContinue
$fonts = [Windows.Media.Fonts]::SystemFontFamilies.Source
$renderingMode = [Environment]::GetEnvironmentVariable('MesIngestWatch__RenderingMode')
$snapshot = [ordered]@{
    CapturedAt = [DateTimeOffset]::Now.ToString('O')
    Hostname = [Environment]::MachineName
    Interactive = [Environment]::UserInteractive
    SessionId = $process.SessionId
    ExplorerInSession = $null -ne $explorer
    InputDesktop = $hasInputDesktop
    DesktopWidth = [GoldenRenderer.NativeMethods]::GetSystemMetrics(0)
    DesktopHeight = [GoldenRenderer.NativeMethods]::GetSystemMetrics(1)
    Dpi = [int][GoldenRenderer.NativeMethods]::GetDpiForSystem()
    ScalePercent = [int]([Math]::Round(
        [GoldenRenderer.NativeMethods]::GetDpiForSystem() * 100 / 96))
    AppsUseLightTheme = $theme.AppsUseLightTheme -eq 1
    Culture = [Globalization.CultureInfo]::CurrentCulture.Name
    UiCulture = [Globalization.CultureInfo]::CurrentUICulture.Name
    TimeZone = [TimeZoneInfo]::Local.Id
    MicrosoftYaHeiUiInstalled = $fonts -contains 'Microsoft YaHei UI'
    ConsolasInstalled = $fonts -contains 'Consolas'
    RenderingMode = $renderingMode
}

$differences = [Collections.Generic.List[string]]::new()
if (-not $snapshot.Interactive) { $differences.Add('interactive session unavailable') }
if (-not $snapshot.ExplorerInSession) { $differences.Add('Explorer is not in the test session') }
if (-not $snapshot.InputDesktop) { $differences.Add('input desktop unavailable') }
if ($snapshot.DesktopWidth -ne $ExpectedDesktopWidth -or
    $snapshot.DesktopHeight -ne $ExpectedDesktopHeight) {
    $differences.Add(
        "expected desktop=${ExpectedDesktopWidth}x${ExpectedDesktopHeight}; " +
        "actual=$($snapshot.DesktopWidth)x$($snapshot.DesktopHeight)")
}
if ($snapshot.Dpi -ne $ExpectedDpi) {
    $differences.Add("expected DPI=$ExpectedDpi; actual=$($snapshot.Dpi)")
}
if (-not $snapshot.AppsUseLightTheme) { $differences.Add('Windows apps theme is not light') }
if ($snapshot.Culture -ne 'zh-CN') { $differences.Add("expected culture=zh-CN; actual=$($snapshot.Culture)") }
if ($snapshot.UiCulture -ne 'zh-CN') { $differences.Add("expected UI culture=zh-CN; actual=$($snapshot.UiCulture)") }
if ($snapshot.TimeZone -ne 'China Standard Time') {
    $differences.Add("expected timezone=China Standard Time; actual=$($snapshot.TimeZone)")
}
if (-not $snapshot.MicrosoftYaHeiUiInstalled) { $differences.Add('Microsoft YaHei UI is missing') }
if (-not $snapshot.ConsolasInstalled) { $differences.Add('Consolas is missing') }
if ($snapshot.RenderingMode -ne 'SoftwareOnly') {
    $differences.Add("expected rendering=SoftwareOnly; actual=$($snapshot.RenderingMode)")
}

$report = [ordered]@{
    Status = if ($differences.Count -eq 0) { 'PASSED' } else { 'FAILED' }
    Expected = [ordered]@{
        DesktopWidth = $ExpectedDesktopWidth
        DesktopHeight = $ExpectedDesktopHeight
        Dpi = $ExpectedDpi
    }
    Actual = $snapshot
    Differences = @($differences)
}
$json = $report | ConvertTo-Json -Depth 5
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $resolvedOutput
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
}
$json

if ($differences.Count -ne 0) {
    [Console]::Error.WriteLine(
        'GOLDEN_RENDERER_ENVIRONMENT_UNAVAILABLE:' + [Environment]::NewLine +
        ($differences | ForEach-Object { "- $_" } | Out-String).TrimEnd())
    exit 2
}

Write-Output 'GOLDEN_RENDERER_ENVIRONMENT_OK'
exit 0
