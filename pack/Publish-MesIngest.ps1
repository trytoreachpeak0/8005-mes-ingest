#Requires -Version 5.1
<#
.SYNOPSIS
  Publish a self-contained MesIngest install directory for factory copy-deploy.

.PARAMETER OutputDir
  Root folder that will contain service/, watch/, queries/, templates/, scripts/, validation/, INSTALL.md, FACTORY-VALIDATION.md, VERSION.txt.

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
$validationSrc = Join-Path $PSScriptRoot "validation"
$releaseValidator = Join-Path $PSScriptRoot "Test-ReleasePackage.ps1"
$releaseSmoke = Join-Path $validationSrc "Invoke-ReleaseSmoke.ps1"
$watchAcceptance = Join-Path $validationSrc "Invoke-WatchAcceptance.ps1"
$openapiSrc = Join-Path $PSScriptRoot "openapi\v1.json"
$installService = Join-Path $PSScriptRoot "install-service.ps1"
$uninstallService = Join-Path $PSScriptRoot "uninstall-service.ps1"

if (-not (Test-Path $hostProj)) { throw "Host project not found: $hostProj" }
if (-not (Test-Path $exampleLocal)) { throw "Missing blank config template: $exampleLocal" }
if (-not (Test-Path $exampleWatchLocal)) { throw "Missing Watch blank config template: $exampleWatchLocal" }
if (-not (Test-Path $upgradeDoc)) { throw "Missing upgrade/rollback doc: $upgradeDoc" }
if (-not (Test-Path $factoryValidationDoc)) { throw "Missing factory validation checklist: $factoryValidationDoc" }
if (-not (Test-Path $validationSrc)) { throw "Missing validation templates: $validationSrc" }
if (-not (Test-Path $releaseValidator)) { throw "Missing release package validator: $releaseValidator" }
if (-not (Test-Path $releaseSmoke)) { throw "Missing packaged release smoke: $releaseSmoke" }
if (-not (Test-Path $watchAcceptance)) { throw "Missing packaged Watch acceptance entry: $watchAcceptance" }
if (-not (Test-Path $openapiSrc)) { throw "Missing static OpenAPI contract: $openapiSrc" }

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
$queriesDir = Join-Path $OutputDir "queries"
$templatesDir = Join-Path $OutputDir "templates"
$scriptsDir = Join-Path $OutputDir "scripts"
$validationDir = Join-Path $OutputDir "validation"
$openapiDir = Join-Path $OutputDir "openapi"

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

# Prefer published queries beside Host; also mirror at install root for operators.
$hostQueries = Join-Path $serviceDir "queries"
if (Test-Path $queriesDir) { Remove-Item -Recurse -Force $queriesDir }
if (Test-Path $hostQueries) {
    Copy-Item -Recurse $hostQueries $queriesDir
} else {
    throw "Published Host is missing queries/ (mes-task-union manuscript copy)."
}

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

if (Test-Path $validationDir) { Remove-Item -Recurse -Force $validationDir }
Copy-Item -Recurse $validationSrc $validationDir

New-Item -ItemType Directory -Force -Path $openapiDir | Out-Null
Copy-Item $openapiSrc (Join-Path $openapiDir "v1.json") -Force

$versionPath = Join-Path $OutputDir "VERSION.txt"
$hostDll = Join-Path $serviceDir "MesIngest.Host.dll"
$hostVer = if (Test-Path $hostDll) {
    [System.Diagnostics.FileVersionInfo]::GetVersionInfo($hostDll).FileVersion
} else {
    "unknown"
}
$sourceCommit = @(& git -C $csharpRoot rev-parse HEAD 2>$null) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($sourceCommit)) { $sourceCommit = "unknown" }
$sourceStatus = @(& git -C $csharpRoot status --porcelain=v1 --untracked-files=normal -- . 2>$null)
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
