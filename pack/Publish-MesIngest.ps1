#Requires -Version 5.1
<#
.SYNOPSIS
  Publish a self-contained MesIngest install directory for factory copy-deploy.

.PARAMETER OutputDir
  Root folder that will contain service/, watch/, templates/, scripts/, validation/, INSTALL.md, FACTORY-VALIDATION.md, VERSION.txt.

.PARAMETER Configuration
  Build configuration (default Release).

.PARAMETER Runtime
  RID for self-contained publish (default win-x64).

.PARAMETER SkipWatch
  If set, omit the optional WPF watch client.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist\MesIngest"),

    [string] $Configuration = "Release",

    [string] $Runtime = "win-x64",

    [switch] $SkipWatch
)

$ErrorActionPreference = "Stop"

$csharpRoot = Split-Path -Parent $PSScriptRoot
$hostProj = Join-Path $csharpRoot "MesIngest.Host\MesIngest.Host.csproj"
$watchProj = Join-Path $csharpRoot "MesIngest.Watch\MesIngest.Watch.csproj"
$exampleLocal = Join-Path $csharpRoot "MesIngest.Host\appsettings.Local.json.example"
$exampleWatchLocal = Join-Path $csharpRoot "MesIngest.Watch\appsettings.Local.json.example"
$installDoc = Join-Path $PSScriptRoot "INSTALL.md"
$upgradeDoc = Join-Path $PSScriptRoot "UPGRADE.md"
$factoryValidationDoc = Join-Path $PSScriptRoot "FACTORY-VALIDATION.md"
$releaseEvidence = Join-Path $PSScriptRoot "RELEASE-EVIDENCE.json"
$validationSrc = Join-Path $PSScriptRoot "validation"
$releaseValidator = Join-Path $PSScriptRoot "Test-ReleasePackage.ps1"
$releaseSmoke = Join-Path $validationSrc "Invoke-ReleaseSmoke.ps1"
$watchAcceptance = Join-Path $validationSrc "Invoke-WatchAcceptance.ps1"
$installService = Join-Path $PSScriptRoot "install-service.ps1"
$uninstallService = Join-Path $PSScriptRoot "uninstall-service.ps1"
$canonicalQuerySource = [IO.Path]::GetFullPath((Join-Path $csharpRoot "..\..\queries\mes-task-union\query.sql"))
$canonicalQueryId = 'MES_TASK_UNION'
$canonicalQuerySha256 = '54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae'
$canonicalQueryRelativePath = 'service/queries/mes-task-union/query.sql'

if (-not (Test-Path $hostProj)) { throw "Host project not found: $hostProj" }
if (-not (Test-Path $exampleLocal)) { throw "Missing blank config template: $exampleLocal" }
if (-not (Test-Path $exampleWatchLocal)) { throw "Missing Watch blank config template: $exampleWatchLocal" }
if (-not (Test-Path $upgradeDoc)) { throw "Missing upgrade/rollback doc: $upgradeDoc" }
if (-not (Test-Path $factoryValidationDoc)) { throw "Missing factory validation checklist: $factoryValidationDoc" }
if (-not (Test-Path $releaseEvidence)) { throw "Missing release evidence index: $releaseEvidence" }
if (-not (Test-Path $validationSrc)) { throw "Missing validation templates: $validationSrc" }
if (-not (Test-Path $releaseValidator)) { throw "Missing release package validator: $releaseValidator" }
if (-not (Test-Path $releaseSmoke)) { throw "Missing packaged release smoke: $releaseSmoke" }
if (-not (Test-Path $watchAcceptance)) { throw "Missing packaged Watch acceptance entry: $watchAcceptance" }
if (-not (Test-Path -LiteralPath $canonicalQuerySource -PathType Leaf)) { throw "Missing canonical query source: $canonicalQuerySource" }

$resolvedOutput = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\', '/')
$pathRoot = [IO.Path]::GetPathRoot($resolvedOutput).TrimEnd('\', '/')
$protectedPaths = @(
    [IO.Path]::GetFullPath($csharpRoot).TrimEnd('\', '/'),
    [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\', '/')
)
if ([string]::IsNullOrWhiteSpace($resolvedOutput) `
    -or $resolvedOutput -eq $pathRoot `
    -or $protectedPaths -contains $resolvedOutput) {
    throw "Refusing unsafe package OutputDir: $resolvedOutput"
}
$OutputDir = $resolvedOutput
$serviceDir = Join-Path $OutputDir "service"
$watchDir = Join-Path $OutputDir "watch"
$templatesDir = Join-Path $OutputDir "templates"
$scriptsDir = Join-Path $OutputDir "scripts"
$validationDir = Join-Path $OutputDir "validation"

if (Test-Path -LiteralPath $OutputDir) {
    Write-Host "Clearing package output -> $OutputDir"
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "Publishing Host -> $serviceDir"
dotnet publish $hostProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --ignore-failed-sources `
    -o $serviceDir `
    /p:PublishSingleFile=false `
    /p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "Host publish failed ($LASTEXITCODE)" }

if (-not $SkipWatch) {
    Write-Host "Publishing Watch -> $watchDir"
    dotnet publish $watchProj `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        --ignore-failed-sources `
        -o $watchDir `
        /p:PublishSingleFile=false `
        /p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw "Watch publish failed ($LASTEXITCODE)" }
}

# The service copy is the only deployable SQL artifact. Verify the repository source and
# published bytes before declaring their content-addressed version beside the artifact.
$canonicalSourceHash = (Get-FileHash -LiteralPath $canonicalQuerySource -Algorithm SHA256).Hash.ToLowerInvariant()
if ($canonicalSourceHash -cne $canonicalQuerySha256) {
    throw "Canonical query source hash mismatch: expected $canonicalQuerySha256; actual $canonicalSourceHash"
}
$publishedCanonicalQuery = Join-Path $OutputDir $canonicalQueryRelativePath
if (-not (Test-Path -LiteralPath $publishedCanonicalQuery -PathType Leaf)) {
    throw "Published Host is missing canonical query: $canonicalQueryRelativePath"
}
$publishedCanonicalQueryFile = Get-Item -LiteralPath $publishedCanonicalQuery
$publishedCanonicalQueryHash = (Get-FileHash -LiteralPath $publishedCanonicalQuery -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publishedCanonicalQueryFile.Length -le 0 -or $publishedCanonicalQueryHash -cne $canonicalQuerySha256) {
    throw "Published canonical query differs from the approved repository source."
}
[ordered]@{
    schemaVersion = 1
    id = $canonicalQueryId
    version = "$canonicalQueryId/sha256:$canonicalQuerySha256"
    path = $canonicalQueryRelativePath
    length = $publishedCanonicalQueryFile.Length
    sha256 = $canonicalQuerySha256
} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $publishedCanonicalQueryFile.DirectoryName 'query.manifest.json') -Encoding UTF8

New-Item -ItemType Directory -Force -Path $templatesDir | Out-Null
Copy-Item $exampleLocal (Join-Path $templatesDir "appsettings.Local.json.example") -Force
Copy-Item $exampleWatchLocal (Join-Path $templatesDir "watch.appsettings.Local.json.example") -Force

# Ensure no filled Local.json leaks into the package.
$leakedLocal = Join-Path $serviceDir "appsettings.Local.json"
foreach ($localConfig in @($leakedLocal, (Join-Path $watchDir "appsettings.Local.json"))) {
    if (Test-Path $localConfig) {
        Remove-Item -Force $localConfig
        Write-Warning "Removed $localConfig from package (credentials must not ship)."
    }
}

New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null
Copy-Item $installService (Join-Path $scriptsDir "install-service.ps1") -Force
Copy-Item $uninstallService (Join-Path $scriptsDir "uninstall-service.ps1") -Force
Copy-Item $releaseValidator (Join-Path $scriptsDir "Test-ReleasePackage.ps1") -Force
Copy-Item $installDoc (Join-Path $OutputDir "INSTALL.md") -Force
Copy-Item $upgradeDoc (Join-Path $OutputDir "UPGRADE.md") -Force
Copy-Item $factoryValidationDoc (Join-Path $OutputDir "FACTORY-VALIDATION.md") -Force
Copy-Item $releaseEvidence (Join-Path $OutputDir "RELEASE-EVIDENCE.json") -Force

if (Test-Path $validationDir) { Remove-Item -Recurse -Force $validationDir }
Copy-Item -Recurse $validationSrc $validationDir

$versionPath = Join-Path $OutputDir "VERSION.txt"
$hostDll = Join-Path $serviceDir "MesIngest.Host.dll"
$hostVer = if (Test-Path $hostDll) {
    [System.Diagnostics.FileVersionInfo]::GetVersionInfo($hostDll).FileVersion
} else {
    "unknown"
}
$sourceCommit = @(& git -C $csharpRoot rev-parse HEAD 2>$null) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = "unknown" }
$sourceStatus = @(& git -C $csharpRoot status --porcelain=v1 --untracked-files=normal -- `
    . `
    ':(exclude).artifacts/**' `
    ':(exclude)MesIngest.Tests/TestResults/**' 2>$null)
$sourceDirty = $sourceStatus.Count -gt 0
@(
    "MesIngest install package"
    "BuiltUtc=$([DateTime]::UtcNow.ToString('o'))"
    "Configuration=$Configuration"
    "Runtime=$Runtime"
    "HostFileVersion=$hostVer"
    "WatchIncluded=$(-not $SkipWatch)"
    "SourceCommit=$sourceCommit"
    "SourceDirty=$sourceDirty"
) | Set-Content -Path $versionPath -Encoding UTF8

$validatorParameters = @{
    PackageRoot = $OutputDir
    ManifestPath = (Join-Path $OutputDir 'RELEASE-MANIFEST.json')
    SourceCommit = $sourceCommit
    SourceDirty = $sourceDirty
    Configuration = $Configuration
    Runtime = $Runtime
}
if ($SkipWatch) { $validatorParameters.AllowNoWatch = $true }
& $releaseValidator @validatorParameters
if ($LASTEXITCODE -ne 0) { throw "Release package validation failed ($LASTEXITCODE)" }

Write-Host "Install package ready: $OutputDir"
Write-Host "Next: copy folder to plant PC, fill templates/*.Local.json.example, see INSTALL.md / UPGRADE.md and FACTORY-VALIDATION.md"
