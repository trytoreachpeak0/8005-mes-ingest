#Requires -Version 7
<#
.SYNOPSIS
    Build a VSTest filter that selects, or excludes, the test classes that need
    an interactive desktop.

.DESCRIPTION
    `MesIngest.Tests` contains classes that drive real WPF windows. They are
    marked `[Collection("WpfDesktop")]`, which already serialises them against
    each other, and they additionally require a session with an interactive
    window station. On a session-0 runner they do not skip — they fail, with
    dispatcher thread-affinity errors that name nothing resembling the real
    cause.

    CI therefore partitions the suite: the headless runner takes everything
    except these classes, the interactive runner takes exactly these. The
    partition is derived from the source rather than hard-coded, because a
    hard-coded list is a list someone will forget to update — a new
    `[Collection("WpfDesktop")]` class would then land on the headless runner and
    fail for reasons that look like a code defect. This was not hypothetical: the
    first attempt excluded two class names by hand and two more classes promptly
    failed the next run.

.PARAMETER Mode
    Exclude (default) for the headless job, Include for the interactive one.

.EXAMPLE
    dotnet test MesIngest.Tests --filter (./Get-WpfDesktopTestFilter.ps1 -Mode Exclude)
#>
[CmdletBinding()]
param(
    [ValidateSet('Include', 'Exclude')]
    [string]$Mode = 'Exclude'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testsRoot = Join-Path $PSScriptRoot 'MesIngest.Tests'
if (-not (Test-Path -LiteralPath $testsRoot)) {
    throw "找不到测试项目目录：$testsRoot"
}

$classes = Get-ChildItem -LiteralPath $testsRoot -Filter *.cs -Recurse -File |
    ForEach-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw
        [regex]::Matches($text, '\[Collection\("WpfDesktop"\)\][\s\S]{0,400}?\bclass\s+(\w+)') |
            ForEach-Object { $_.Groups[1].Value }
    } |
    Sort-Object -Unique

if ($classes.Count -eq 0) {
    throw '没有找到任何 [Collection("WpfDesktop")] 的测试类。标记改名了，还是这个脚本跑错了目录？'
}

Write-Verbose "桌面测试类 $($classes.Count) 个：$($classes -join ', ')"

if ($Mode -eq 'Exclude') {
    ($classes | ForEach-Object { "FullyQualifiedName!~$_" }) -join '&'
}
else {
    ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
}
