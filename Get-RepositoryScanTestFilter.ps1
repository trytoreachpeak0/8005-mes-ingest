#Requires -Version 7
<#
.SYNOPSIS
    Build a VSTest filter that selects the test classes which read the whole
    repository as data.

.DESCRIPTION
    A few tests do not test code, they test the tree: they walk every file
    outside the build and evidence directories and fail if a retired contract
    name reaches a shipped artifact, if an unattended script can drop a database,
    or if anything resembling a real credential appears in a `.md`. That last one
    is why documentation cannot simply be excluded from CI — operator
    documentation is exactly where a password gets pasted.

    `repo-scan.yml` runs these, and only these, when a push touches nothing but
    documentation, so `test.yml` can ignore those paths without losing the scan.
    The two workflows partition the trigger space; neither drops it.

    The class list is derived from the source rather than written into the
    workflow, for the same reason `Get-WpfDesktopTestFilter.ps1` derives its
    partition: a hard-coded list is a list someone will forget to update. Here
    the failure would be silent rather than loud — a new repository-walking test
    would keep passing while quietly never running on the changes it exists to
    check.

    The marker is `SearchOption.AllDirectories`. A test that enumerates the tree
    that way is reading the repository as data; one that does not, is not.

    A file that carries the marker can also hold helpers that are not tests —
    `RetiredContractAndCutoverSafetyTests.cs` defines `RepositoryPaths` — so a
    class counts only when a `[Fact]` or `[Theory]` appears between its
    declaration and the next one. Naming is deliberately not the test: every test
    class here happens to end in `Tests` today, and a convention that holds today
    is a convention, not a check.

.EXAMPLE
    dotnet test MesIngest.Tests --filter (./Get-RepositoryScanTestFilter.ps1)
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testsRoot = Join-Path $PSScriptRoot 'MesIngest.Tests'
if (-not (Test-Path -LiteralPath $testsRoot)) {
    throw "找不到测试项目目录：$testsRoot"
}

$classPattern = '(?m)^\s*(?:public|internal)\s+(?:sealed\s+|static\s+|partial\s+|abstract\s+)*class\s+(\w+)'

$classes = Get-ChildItem -LiteralPath $testsRoot -Filter *.cs -Recurse -File |
    ForEach-Object {
        $text = Get-Content -LiteralPath $_.FullName -Raw
        if ($text -notmatch 'SearchOption\.AllDirectories') { return }

        $declarations = @([regex]::Matches($text, $classPattern))
        for ($i = 0; $i -lt $declarations.Count; $i++) {
            $start = $declarations[$i].Index + $declarations[$i].Length
            $end = if ($i + 1 -lt $declarations.Count) { $declarations[$i + 1].Index } else { $text.Length }
            $body = $text.Substring($start, $end - $start)
            if ($body -match '\[(?:Fact|Theory)\b') {
                $declarations[$i].Groups[1].Value
            }
        }
    } |
    Sort-Object -Unique

if ($classes.Count -eq 0) {
    throw '没有找到任何遍历仓库的测试类。SearchOption.AllDirectories 这个标记还在用吗，还是这个脚本跑错了目录？'
}

Write-Verbose "仓库扫描类 $($classes.Count) 个：$($classes -join ', ')"

($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
