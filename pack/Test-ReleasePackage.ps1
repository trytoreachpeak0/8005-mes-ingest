#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageRoot,

    [string] $ManifestPath,

    [string] $SourceCommit = "unknown",

    [bool] $SourceDirty = $true,

    [string] $Configuration = "unknown",

    [string] $Runtime = "unknown",

    [switch] $AllowNoWatch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $PackageRoot -ErrorAction Stop).Path
$manifest = if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    Join-Path $root "RELEASE-MANIFEST.json"
} else {
    [IO.Path]::GetFullPath($ManifestPath)
}
if (-not $manifest.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ManifestPath must stay inside PackageRoot: $manifest"
}
if (Test-Path -LiteralPath $manifest) {
    Remove-Item -LiteralPath $manifest -Force
}

$canonicalQueryId = 'MES_TASK_UNION'
$canonicalQuerySha256 = '54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae'
$canonicalQueryVersion = "$canonicalQueryId/sha256:$canonicalQuerySha256"
$canonicalQueryRelativePath = 'service/queries/mes-task-union/query.sql'
$canonicalQueryManifestRelativePath = 'service/queries/mes-task-union/query.manifest.json'

$required = @(
    "service\MesIngest.Host.exe",
    "templates\appsettings.Local.json.example",
    "templates\watch.appsettings.Local.json.example",
    "scripts\install-service.ps1",
    "scripts\uninstall-service.ps1",
    "scripts\Test-ReleasePackage.ps1",
    "validation\Invoke-FactoryValidation.ps1",
    "validation\Invoke-ReleaseSmoke.ps1",
    "validation\Invoke-WatchAcceptance.ps1",
    "INSTALL.md",
    "UPGRADE.md",
    "FACTORY-VALIDATION.md",
    "RELEASE-EVIDENCE.json",
    "VERSION.txt",
    $canonicalQueryRelativePath,
    $canonicalQueryManifestRelativePath
)
if (-not $AllowNoWatch) {
    $required += "watch\MesIngest.Watch.exe"
}
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    $missingCanonical = @($missing | Where-Object { $_ -in @($canonicalQueryRelativePath, $canonicalQueryManifestRelativePath) })
    if ($missingCanonical.Count -gt 0) {
        throw "Release package is missing canonical query files: $($missingCanonical -join ', ')"
    }
    throw "Release package is missing required files: $($missing -join ', ')"
}

$queryFiles = @(Get-ChildItem -LiteralPath $root -Filter "*.sql" -File -Force -Recurse -ErrorAction SilentlyContinue)
if ($queryFiles.Count -ne 1) {
    throw "Release package must contain exactly one canonical SQL artifact; found $($queryFiles.Count)."
}

$canonicalQueryPath = Join-Path $root $canonicalQueryRelativePath
$actualQueryPath = $queryFiles[0].FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
if ($actualQueryPath -cne $canonicalQueryRelativePath) {
    throw "The only SQL artifact must use the canonical path '$canonicalQueryRelativePath'; found '$actualQueryPath'."
}
$canonicalQueryFile = Get-Item -LiteralPath $canonicalQueryPath
if ($canonicalQueryFile.Length -le 0) {
    throw 'Canonical SQL artifact must not be empty.'
}
$canonicalQueryActualSha256 = (Get-FileHash -LiteralPath $canonicalQueryPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($canonicalQueryActualSha256 -cne $canonicalQuerySha256) {
    throw "Canonical SQL artifact hash mismatch: expected $canonicalQuerySha256; actual $canonicalQueryActualSha256."
}

$canonicalQueryManifestPath = Join-Path $root $canonicalQueryManifestRelativePath
try {
    $canonicalQueryDeclaration = (Get-Content -Raw -LiteralPath $canonicalQueryManifestPath) | ConvertFrom-Json
} catch {
    throw "Canonical query manifest is invalid JSON: $canonicalQueryManifestRelativePath"
}
if ([int]$canonicalQueryDeclaration.schemaVersion -ne 1 `
    -or [string]$canonicalQueryDeclaration.id -cne $canonicalQueryId `
    -or [string]$canonicalQueryDeclaration.version -cne $canonicalQueryVersion `
    -or [string]$canonicalQueryDeclaration.path -cne $canonicalQueryRelativePath `
    -or [long]$canonicalQueryDeclaration.length -ne $canonicalQueryFile.Length `
    -or ([string]$canonicalQueryDeclaration.sha256).ToLowerInvariant() -cne $canonicalQuerySha256) {
    throw "Canonical query manifest does not match the approved artifact: $canonicalQueryManifestRelativePath"
}

$files = @(Get-ChildItem -LiteralPath $root -File -Recurse)
$forbidden = @($files | Where-Object {
    $relative = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('/', '\')
    $relative -match '(^|\\)(?:MesIngest\.Watch\.Prototype|TestResults|Baselines|WindowBaselines)(\\|$)' `
        -or $_.Name -match '(?i)(FakeHost|\.received\.|\.verified\.(?:png|xml)$)' `
        -or $_.Extension -in @('.cs', '.csproj', '.sln') `
        -or $_.Name -match '^MesIngest\.(?:Tests|Watch\.UiTests)(?:\.|$)'
})
if ($forbidden.Count -gt 0) {
    $names = @($forbidden | ForEach-Object { $_.FullName.Substring($root.Length).TrimStart('\', '/') })
    throw "Release package contains forbidden prototype, test fake, source, or visual-candidate files: $($names -join ', ')"
}

$filledLocal = @($files | Where-Object { $_.Name -eq 'appsettings.Local.json' })
if ($filledLocal.Count -gt 0) {
    throw "Release package contains forbidden filled appsettings.Local.json files."
}

$watchTemplatePath = Join-Path $root "templates\watch.appsettings.Local.json.example"
$watchTemplateText = Get-Content -Raw -LiteralPath $watchTemplatePath
$watchTemplate = $watchTemplateText | ConvertFrom-Json
if ($watchTemplate.Watch.BaseUrl -ne 'http://127.0.0.1:5088') {
    throw "Watch template must describe exactly one default Host base URL on loopback."
}
if ([int]$watchTemplate.Watch.RequestTimeoutSeconds -lt 1 -or [int]$watchTemplate.Watch.RequestTimeoutSeconds -gt 300) {
    throw "Watch template RequestTimeoutSeconds must stay within 1..300."
}
if ([int]$watchTemplate.Watch.ConnectionLogRetentionDays -lt 1 `
    -or [int]$watchTemplate.Watch.ConnectionLogMaxSizeMb -lt 1) {
    throw "Watch template must include positive connection-log retention limits."
}
if ($watchTemplate.Watch.RenderingMode -ne 'SoftwareOnly') {
    throw "Watch template must default RenderingMode to SoftwareOnly."
}
if (-not [string]::IsNullOrEmpty([string]$watchTemplate.Watch.SharedSecret)) {
    throw "Watch template must not contain a SharedSecret."
}
foreach ($text in @('MesIngestWatch__SharedSecret', 'external-configuration', '%LocalAppData%')) {
    if ($watchTemplateText.IndexOf($text, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Watch template is missing secure configuration or local-preference guidance: $text"
    }
}

$hostTemplatePath = Join-Path $root "templates\appsettings.Local.json.example"
$hostTemplate = (Get-Content -Raw -LiteralPath $hostTemplatePath) | ConvertFrom-Json
if (-not [string]::IsNullOrEmpty([string]$hostTemplate.MesIngest.SharedSecret)) {
    throw "Host template must not contain a SharedSecret."
}
if ([string]::IsNullOrWhiteSpace([string]$hostTemplate.MesIngest.NewSqlServerConnectionString)) {
    throw "Host template must contain the production NewSqlServerConnectionString placeholder."
}

$releaseEvidencePath = Join-Path $root 'RELEASE-EVIDENCE.json'
$releaseEvidence = (Get-Content -Raw -LiteralPath $releaseEvidencePath) | ConvertFrom-Json
if ($releaseEvidence.rebuildDecision -ne '2026-08-09' `
    -or $releaseEvidence.oldVisualEvidenceAccepted -ne $false `
    -or $releaseEvidence.tickets.'11'.xamlScenarioCount -ne 19 `
    -or $releaseEvidence.tickets.'12'.realWindowBaselineCount -ne 5 `
    -or $releaseEvidence.tickets.'12'.completeGateRuns -ne 50 `
    -or $releaseEvidence.tickets.'13'.generation -ne '2026-08-09-rebuild') {
    throw 'Release evidence must pin the Ticket 11/12/13 rebuild lineage and reject old visual evidence.'
}
$inventory = @(
    Get-ChildItem -LiteralPath $root -File -Recurse |
        Where-Object { $_.FullName -ne $manifest } |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        } |
        Sort-Object path
)
[ordered]@{
    schemaVersion = 2
    validationStatus = 'PASSED'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    sourceCommit = $SourceCommit
    sourceDirty = $SourceDirty
    configuration = $Configuration
    runtime = $Runtime
    businessApiMethods = @('GET')
    productionSurface = '/api/v2/*'
    contractDiscovery = '/api/v2/contract'
    openApiStatus = 'DEFERRED_TO_TICKET_17'
    canonicalQuery = [ordered]@{
        id = $canonicalQueryId
        version = $canonicalQueryVersion
        path = $canonicalQueryRelativePath
        length = $canonicalQueryFile.Length
        sha256 = $canonicalQuerySha256
    }
    rebuildEvidence = $releaseEvidence
    files = $inventory
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest -Encoding UTF8

Write-Output "MESINGEST_RELEASE_PACKAGE_VALID: root=$root files=$($inventory.Count) manifest=$manifest"
