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
$canonicalOpenApiRelativePath = 'openapi/v2.json'
$expectedContractVersion = '2026.08.new-mes-ingest.v2.1'
$expectedContractSchemaVersion = 28
$expectedOpenApiPaths = @(
    '/api/v2/absence-authority',
    '/api/v2/absence-authority/{hostSessionId}',
    '/api/v2/contract',
    '/api/v2/current-ingest-attention',
    '/api/v2/demand-series',
    '/api/v2/demand-series/by-key',
    '/api/v2/demand-series/{seriesId}',
    '/api/v2/error-search',
    '/api/v2/error-search/{seriesId}',
    '/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations',
    '/api/v2/externally-readable-demand-catalog',
    '/api/v2/poll-traces/{pollTraceId}',
    '/api/v2/readability-audit',
    '/api/v2/readability-audit/{demandId}',
    '/api/v2/task-type-protections',
    '/api/v2/task-type-protections/{workType}',
    '/api/v2/watch-overview'
)

$required = @(
    "service\MesIngest.Host.exe",
    "administration\MesIngest.LocalAdministration.exe",
    "templates\appsettings.Local.json.example",
    "templates\watch.appsettings.Local.json.example",
    "scripts\install-service.ps1",
    "scripts\uninstall-service.ps1",
    "scripts\Test-ReleasePackage.ps1",
    "scripts\cutover\CutoverSqlTools.ps1",
    "scripts\cutover\Invoke-EmptyDatabaseCutover.ps1",
    "scripts\cutover\Invoke-CutoverRollback.ps1",
    "scripts\maintenance\Invoke-SqlServerMemoryProfile.ps1",
    "validation\Invoke-FactoryValidation.ps1",
    "validation\Invoke-ReleaseSmoke.ps1",
    "validation\release-smoke-rounds.json",
    "validation\Invoke-WatchAcceptance.ps1",
    "validation\Invoke-FactoryAcceptance.ps1",
    "validation\FactoryAcceptanceTools.ps1",
    "validation\WatchAcceptanceUia.ps1",
    "INSTALL.md",
    "UPGRADE.md",
    "FACTORY-VALIDATION.md",
    "RELEASE-EVIDENCE.json",
    "VERSION.txt",
    $canonicalOpenApiRelativePath,
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

$canonicalOpenApiPath = Join-Path $root $canonicalOpenApiRelativePath
$canonicalOpenApiText = Get-Content -Raw -LiteralPath $canonicalOpenApiPath
try {
    $canonicalOpenApi = $canonicalOpenApiText | ConvertFrom-Json
} catch {
    throw "Canonical V2 OpenAPI is invalid JSON: $canonicalOpenApiRelativePath"
}
$openApiVersionIsSupported = [string]$canonicalOpenApi.openapi -match '^3\.'
$openApiContractIdentityMatches =
    [string]$canonicalOpenApi.info.version -ceq $expectedContractVersion
if (-not $openApiVersionIsSupported -or -not $openApiContractIdentityMatches) {
    throw "Canonical V2 OpenAPI identity mismatch: expected contract $expectedContractVersion."
}
if ($null -eq $canonicalOpenApi.paths -or $null -eq $canonicalOpenApi.components.schemas) {
    throw 'Canonical V2 OpenAPI must define paths and component schemas.'
}

$actualOpenApiPaths = @(
    $canonicalOpenApi.paths.PSObject.Properties |
        ForEach-Object { $_.Name } |
        Sort-Object
)
$openApiPathDifference = @(
    Compare-Object -ReferenceObject $expectedOpenApiPaths -DifferenceObject $actualOpenApiPaths -CaseSensitive
)
if ($openApiPathDifference.Count -gt 0) {
    throw 'Canonical V2 OpenAPI paths must exactly match the frozen /api/v2 surface.'
}
foreach ($pathProperty in $canonicalOpenApi.paths.PSObject.Properties) {
    if (-not $pathProperty.Name.StartsWith('/api/v2/', [StringComparison]::Ordinal)) {
        throw "Canonical V2 OpenAPI contains a legacy or non-V2 path: $($pathProperty.Name)"
    }
    $pathMembers = @($pathProperty.Value.PSObject.Properties | ForEach-Object { $_.Name })
    if ($pathMembers.Count -ne 1 -or $pathMembers[0] -cne 'get') {
        throw "Canonical V2 OpenAPI permits only GET operations: $($pathProperty.Name)"
    }
    if ([string]::IsNullOrWhiteSpace([string]$pathProperty.Value.get.operationId)) {
        throw "Canonical V2 OpenAPI operation is missing operationId: $($pathProperty.Name)"
    }
}
$forbiddenOpenApiTerms = @(
    '/api/contract',
    '/api/demands',
    '/api/alerts',
    '/api/poll-health',
    '/api/demand-changes',
    'DemandSeriesIssue',
    'DemandChangeFeed',
    'FeedSequence',
    'feed sequence',
    'HighWatermark',
    'high-watermark',
    'SYNC_CURSOR_EXPIRED',
    'REAPPEAR_AFTER_GONE',
    'DUPLICATE_RECONCILE_KEY',
    'FrozenMesFieldSet',
    'FIELD_DRIFT',
    'IngestAlert',
    'incident',
    'OccurrenceCount',
    'Fingerprint'
)
foreach ($term in $forbiddenOpenApiTerms) {
    if ($canonicalOpenApiText.IndexOf($term, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Canonical V2 OpenAPI contains a forbidden legacy term: $term"
    }
}
$canonicalOpenApiSha256 = (
    Get-FileHash -LiteralPath $canonicalOpenApiPath -Algorithm SHA256
).Hash.ToLowerInvariant()

# Ticket 24: Service and Watch must implement one versioned contract. NewMesIngestContract
# (version, schema version, capability inventory) lives in MesIngest.Core, so identical
# published bytes on both sides are the mechanical proof that neither drifted.
$contractAssemblyRelativePath = 'MesIngest.Core.dll'
$serviceContractAssembly = Join-Path $root (Join-Path 'service' $contractAssemblyRelativePath)
$watchContractAssembly = Join-Path $root (Join-Path 'watch' $contractAssemblyRelativePath)
if (-not (Test-Path -LiteralPath $serviceContractAssembly -PathType Leaf)) {
    throw "Release package is missing the shared contract assembly: service\$contractAssemblyRelativePath"
}
$sharedContractSha256 = (
    Get-FileHash -LiteralPath $serviceContractAssembly -Algorithm SHA256
).Hash.ToLowerInvariant()
if (-not $AllowNoWatch) {
    if (-not (Test-Path -LiteralPath $watchContractAssembly -PathType Leaf)) {
        throw "Release package is missing the shared contract assembly: watch\$contractAssemblyRelativePath"
    }
    $watchContractSha256 = (
        Get-FileHash -LiteralPath $watchContractAssembly -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($watchContractSha256 -cne $sharedContractSha256) {
        throw 'Packaged Service and Watch do not carry the same versioned contract assembly.'
    }
}

# Ticket 24: the package must not carry a retired endpoint, a legacy-only
# configuration key, or an explanation of the old contract. This scans the
# operator-facing documentation and configuration. Packaged PowerShell still names
# legacy routes inside its own forbidden/404 lists, so it is gated by the
# repository tests for those files rather than by this text scan.
$legacyScanRelativePaths = @(
    'INSTALL.md',
    'UPGRADE.md',
    'FACTORY-VALIDATION.md',
    'RELEASE-EVIDENCE.json',
    'templates\appsettings.Local.json.example',
    'templates\watch.appsettings.Local.json.example',
    'service\appsettings.json',
    'watch\appsettings.json'
)
$legacyScanRelativePaths += @(
    Get-ChildItem -LiteralPath (Join-Path $root 'validation') -Filter '*.md' -File -ErrorAction SilentlyContinue |
        ForEach-Object { "validation\$($_.Name)" }
)
$forbiddenPackageTerms = @(
    '/api/contract',
    '/api/demands',
    '/api/alerts',
    '/api/poll-health',
    '/api/demand-changes',
    'openapi/v1\.json',
    'DemandChangeFeed',
    'SYNC_CURSOR_EXPIRED',
    'high[-]?watermark',
    'REAPPEAR_AFTER_GONE',
    'DUPLICATE_RECONCILE_KEY',
    'IngestAlert',
    'FrozenMesFieldSet',
    'FIELD_DRIFT',
    'OccurrenceCount',
    'ChangeFeedRetentionHours',
    'AlertRetentionDays',
    'SnapshotCsvPath',
    'EnableLegacyDevelopmentEndpoints',
    'GoLiveBaseline',
    'DisappearThreshold',
    # NewSqlServerConnectionString is the production key and must not trip this.
    '(?<!New)SqlServerConnectionString'
)
$retirementMarkers = '404|legacy|development|forbidden|must not|不得|不能|不属于|已移除|禁止|退役'
foreach ($relativeScanPath in $legacyScanRelativePaths) {
    $scanPath = Join-Path $root $relativeScanPath
    if (-not (Test-Path -LiteralPath $scanPath -PathType Leaf)) {
        continue
    }
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($scanPath)) {
        $lineNumber++
        if ($line -imatch $retirementMarkers) {
            continue
        }
        foreach ($forbiddenTerm in $forbiddenPackageTerms) {
            if ($line -imatch $forbiddenTerm) {
                throw ("Release package still teaches the retired contract at " +
                    "${relativeScanPath}:${lineNumber} ($forbiddenTerm). Remove it, or state on the " +
                    'same line that it is retired.')
            }
        }
    }
}

# Ticket 25: database deletion may exist only in the attended cutover drill. Any other
# packaged script that can DROP a database, and any bypass of the typed target
# confirmation, would be an unattended path against an unconfirmed instance.
$packagedScripts = @(
    Get-ChildItem -LiteralPath $root -Filter '*.ps1' -File -Recurse -ErrorAction SilentlyContinue)
$cutoverEntryPoint = Join-Path $root 'scripts\cutover\Invoke-EmptyDatabaseCutover.ps1'
$cutoverToolsPath = Join-Path $root 'scripts\cutover\CutoverSqlTools.ps1'
$rollbackEntryPoint = Join-Path $root 'scripts\cutover\Invoke-CutoverRollback.ps1'
$dropCapablePaths = @(
    $packagedScripts |
        Where-Object { [IO.File]::ReadAllText($_.FullName) -imatch 'DROP\s+DATABASE' } |
        ForEach-Object { $_.FullName })
$unexpectedDropPaths = @(
    $dropCapablePaths | Where-Object { $_ -ne $cutoverEntryPoint -and $_ -ne $cutoverToolsPath })
if ($unexpectedDropPaths.Count -gt 0) {
    throw ('Release package ships a database-deleting path outside the attended cutover drill: ' +
        ($unexpectedDropPaths -join '; '))
}
foreach ($drillPath in @($cutoverEntryPoint, $rollbackEntryPoint)) {
    $drillText = [IO.File]::ReadAllText($drillPath)
    if ($drillText -notmatch 'Assert-CutoverOperatorConfirmation') {
        throw "Cutover drill does not require the typed target confirmation: $drillPath"
    }
    if ($drillText -imatch '\[switch\]\s*\$(Force|Yes|NonInteractive|Unattended)') {
        throw "Cutover drill exposes an unattended bypass switch: $drillPath"
    }
}
$cutoverToolsText = [IO.File]::ReadAllText($cutoverToolsPath)
if ($cutoverToolsText -notmatch 'Read-Host') {
    throw 'Cutover confirmation is not read from the console; an unattended run could satisfy it.'
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
    schemaVersion = 3
    validationStatus = 'PASSED'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    sourceCommit = $SourceCommit
    sourceDirty = $SourceDirty
    configuration = $Configuration
    runtime = $Runtime
    businessApiMethods = @('GET')
    productionSurface = '/api/v2/*'
    contractDiscovery = '/api/v2/contract'
    openApiStatus = 'FROZEN'
    openApi = [ordered]@{
        path = $canonicalOpenApiRelativePath
        contractVersion = $expectedContractVersion
        schemaVersion = $expectedContractSchemaVersion
        sha256 = $canonicalOpenApiSha256
    }
    sharedContract = [ordered]@{
        assembly = $contractAssemblyRelativePath
        sha256 = $sharedContractSha256
        serviceAndWatchIdentical = (-not $AllowNoWatch)
    }
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
