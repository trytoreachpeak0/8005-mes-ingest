#Requires -Version 5.1
<#
.SYNOPSIS
  Decision helpers for the ticket 26 factory acceptance run.

.DESCRIPTION
  Dot-source this file; it defines functions and runs nothing. Everything that decides
  what may be written down as a factory pass lives here, so the repository can exercise
  the shipped logic instead of trusting a one-shot plant run to be right the first time.

  Three rules are enforced rather than documented:
    * evidence that never reached the live plant Oracle cannot be recorded as a pass;
    * a check that did not run must become a named skip with an owner, a prerequisite,
      and the release gate it leaves open — silence is not an option;
    * nothing leaves the plant carrying a credential, a bearer token, or a datasource.
#>

Set-StrictMode -Version Latest

# The Host default when appsettings does not set one. Recorded, never assumed silently.
$script:DefaultOracleCommandTimeoutSeconds = 30

<#
.SYNOPSIS
  Remove credentials, bearer tokens, datasources and host addresses from free text.
#>
function Protect-FactoryAcceptanceText {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Text)

    if ([string]::IsNullOrEmpty($Text)) { return $Text }

    $clean = $Text -replace '(?i)(Authorization\s*:\s*Bearer\s+)\S+', '$1[redacted]'
    $clean = $clean -replace '(?i)\b(Bearer)\s+[A-Za-z0-9\-\._~\+/=]{6,}', '$1 [redacted]'
    $clean = $clean -replace '(?i)((?:SharedSecret|Password|Pwd|Secret|Token)\s*[:=]\s*)[^;\s,)"]+', '$1[redacted]'
    $clean = $clean -replace '(?i)((?:Data Source|Server|Address|Host|Initial Catalog|Database|User Id|User|UID|DSN|Service_Name|SID)\s*=\s*)[^;\s,)"]+', '$1[redacted]'
    # Backstop for a provider message that names an address outside any keyword form.
    $clean = $clean -replace '\b\d{1,3}(?:\.\d{1,3}){3}\b', '[redacted]'
    return $clean
}

<#
.SYNOPSIS
  Turn one --probe-oracle log into acceptance state.

.DESCRIPTION
  Mirrors OracleRoundProbeManifestState: only a live, connected, canonical-query success
  that also exited zero may be PASSED. Offline, simulated and malformed logs become
  NOT_EXECUTED; a live attempt that did something else becomes FAILED.
#>
function Get-LiveOracleProbeState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Thin', 'Thick')][string] $ExpectedMode,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $ProbeOutput,
        [Parameter(Mandatory = $true)][string] $ExpectedQueryVersion,
        [Parameter(Mandatory = $true)][string] $ExpectedQuerySha256,
        [int] $ExitCode = 0,
        [string] $Log = ''
    )

    $fields = @{}
    foreach ($line in ($ProbeOutput -split "`r?`n")) {
        $parts = $line.Split('=', 2)
        if ($parts.Length -eq 2 -and $parts[0].Trim().Length -gt 0) {
            $fields[$parts[0].Trim().ToLowerInvariant()] = $parts[1].Trim()
        }
    }

    function Read-Field {
        param([string] $Name, [string] $Fallback = 'UNKNOWN')

        if ($fields.ContainsKey($Name) -and -not [string]::IsNullOrWhiteSpace($fields[$Name])) {
            return [string]$fields[$Name]
        }
        return $Fallback
    }

    $scope = (Read-Field 'execution_scope' 'NOT_EXECUTED').ToUpperInvariant()
    $connectionAttempted = [string]::Equals(
        (Read-Field 'connection_attempted' 'false'), 'true', [StringComparison]::OrdinalIgnoreCase)
    $requestedMode = Read-Field 'requested_mode' $ExpectedMode
    $actualMode = Read-Field 'actual_mode' 'NOT_AVAILABLE'
    $queryId = Read-Field 'query_id' 'NOT_AVAILABLE'
    $queryVersion = Read-Field 'query_version' 'NOT_AVAILABLE'
    $querySha256 = Read-Field 'query_sha256' 'NOT_AVAILABLE'
    $outcome = Read-Field 'outcome' 'NOT_EXECUTED'
    $rowCount = 0
    [void][int]::TryParse((Read-Field 'row_count' '0'), [ref] $rowCount)
    $durationMs = 0
    [void][int]::TryParse((Read-Field 'duration_ms' '0'), [ref] $durationMs)
    $result = (Read-Field 'result' 'NOT_EXECUTED').ToUpperInvariant()

    if ($result -notin @('PASSED', 'FAILED', 'NOT_EXECUTED')) {
        $result = 'FAILED'
    } elseif ($result -eq 'PASSED' -and ($scope -cne 'LIVE_ORACLE' -or -not $connectionAttempted)) {
        # Offline artifact checks and simulated identities never reached the plant.
        $result = 'NOT_EXECUTED'
    } elseif ($result -eq 'PASSED' -and (
        $ExitCode -ne 0 -or
        -not [string]::Equals($requestedMode, $ExpectedMode, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($actualMode, $ExpectedMode, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($outcome, 'Success', [StringComparison]::OrdinalIgnoreCase) -or
        $queryId -cne 'MES_TASK_UNION' -or
        $queryVersion -cne $ExpectedQueryVersion -or
        $querySha256 -cne $ExpectedQuerySha256)) {
        $result = 'FAILED'
    }

    return [pscustomobject][ordered]@{
        Attempted = $true
        ConnectionAttempted = $connectionAttempted
        ExecutionScope = $scope
        RequestedMode = $requestedMode
        ActualMode = $actualMode
        Driver = Read-Field 'driver' 'NOT_AVAILABLE'
        QueryId = $queryId
        QueryVersion = $queryVersion
        QuerySha256 = $querySha256
        Outcome = $outcome
        RowCount = $rowCount
        DurationMs = $durationMs
        ExitCode = $ExitCode
        Result = $result
        Log = $Log
    }
}

<#
.SYNOPSIS
  Measure the read-only boundary of the statement the Host is allowed to execute.
#>
function Test-CanonicalReadOnlyStatement {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Sql,
        [Parameter(Mandatory = $true)][string] $ExpectedSha256
    )

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $actualSha256 = ([BitConverter]::ToString(
            $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Sql)))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }

    # The artifact documents its own read-only rule in a header comment, so the scan has
    # to look at executable text only or it would flag the rule as a violation.
    $executable = [Regex]::Replace($Sql, '/\*.*?\*/', ' ', [Text.RegularExpressions.RegexOptions]::Singleline)
    $executable = [Regex]::Replace($executable, '--[^\r\n]*', ' ')

    $writeKeywords = @(
        'INSERT', 'UPDATE', 'DELETE', 'MERGE', 'TRUNCATE', 'CREATE', 'ALTER', 'DROP',
        'GRANT', 'REVOKE', 'COMMIT', 'ROLLBACK', 'LOCK', 'EXECUTE', 'CALL')
    $found = New-Object System.Collections.ArrayList
    foreach ($keyword in $writeKeywords) {
        if ([Regex]::IsMatch($executable, "\b$keyword\b", [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
            [void]$found.Add($keyword)
        }
    }
    if ([Regex]::IsMatch($executable, '\bFOR\s+UPDATE\b', [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        [void]$found.Add('FOR UPDATE')
    }

    $statementCount = @(
        $executable -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
    $unionAllCount = [Regex]::Matches(
        $executable, '\bUNION\s+ALL\b', [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count
    $branchCount = [Regex]::Matches(
        $executable, "'[^']*'\s+AS\s+TASK_TYPE", [Text.RegularExpressions.RegexOptions]::IgnoreCase).Count
    $matchesApproved = $actualSha256 -ceq $ExpectedSha256.ToLowerInvariant()

    return [pscustomobject][ordered]@{
        Sha256 = $actualSha256
        MatchesApprovedArtifact = $matchesApproved
        StatementCount = $statementCount
        UnionAllCount = $unionAllCount
        TaskTypeBranchCount = $branchCount
        WriteKeywordsFound = @($found)
        ReadOnly = ($found.Count -eq 0 -and $statementCount -eq 1 -and $matchesApproved)
    }
}

<#
.SYNOPSIS
  Refuse to start an acceptance run against anything but the live Oracle round source.
#>
function Assert-LiveRoundSourceConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $SettingsPath,
        [Parameter(Mandatory = $true)][ValidateSet('Thin', 'Thick')][string] $ExpectedMode
    )

    if (-not (Test-Path -LiteralPath $SettingsPath -PathType Leaf)) {
        throw "LIVE_ORACLE_SETTINGS_MISSING: $SettingsPath"
    }
    try {
        $settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
    } catch {
        throw "LIVE_ORACLE_SETTINGS_INVALID: $SettingsPath"
    }

    $section = $settings.PSObject.Properties['MesIngest']
    if ($null -eq $section -or $null -eq $section.Value) {
        throw "LIVE_ORACLE_SETTINGS_INVALID: $SettingsPath"
    }
    $mesIngest = $section.Value

    function Read-Setting {
        param([string] $Name)

        $property = $mesIngest.PSObject.Properties[$Name]
        if ($null -eq $property) { return $null }
        return $property.Value
    }

    # A recording drives the same production entry, which is precisely why it cannot be
    # allowed here: replayed rounds are release-smoke evidence, never factory acceptance.
    $recording = [string](Read-Setting 'ReplayRoundsFromRecordingPath')
    if (-not [string]::IsNullOrWhiteSpace($recording)) {
        throw 'RECORDED_ROUNDS_ARE_NOT_FACTORY_EVIDENCE: remove ReplayRoundsFromRecordingPath before a factory acceptance run.'
    }

    $snapshotSource = [string](Read-Setting 'SnapshotSource')
    if ($snapshotSource -cne 'Oracle') {
        throw "LIVE_ORACLE_ROUND_SOURCE_REQUIRED: SnapshotSource is '$snapshotSource'."
    }

    $mode = [string](Read-Setting 'OracleMode')
    if ([string]::IsNullOrWhiteSpace($mode)) { $mode = 'Thin' }
    if (-not [string]::Equals($mode, $ExpectedMode, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ORACLE_MODE_MISMATCH: configured '$mode', expected '$ExpectedMode'."
    }

    $timeout = Read-Setting 'OracleCommandTimeoutSeconds'
    $commandTimeout = $script:DefaultOracleCommandTimeoutSeconds
    if ($null -ne $timeout -and [int]$timeout -gt 0) {
        $commandTimeout = [int]$timeout
    }

    return [pscustomobject][ordered]@{
        Mode = $ExpectedMode
        SnapshotSource = $snapshotSource
        CommandTimeoutSeconds = $commandTimeout
        CommandTimeoutIsHostDefault = ($null -eq $timeout)
        RecordingConfigured = $false
    }
}

<#
.SYNOPSIS
  Prove a failed or structurally incomplete round did not move the business projection.
#>
function Test-NonSuccessRoundsPreservedProjection {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Rounds)

    $offenders = New-Object System.Collections.ArrayList
    $unverifiable = New-Object System.Collections.ArrayList
    $nonSuccessCount = 0
    $previous = $null

    foreach ($round in $Rounds) {
        $outcome = ([string]$round.outcome).ToUpperInvariant()
        if ($outcome -cne 'SUCCESS') {
            $nonSuccessCount++
            if ($null -eq $previous) {
                # Nothing committed before it, so there is no projection it could have moved.
                [void]$unverifiable.Add([string]$round.pollTraceId)
            } elseif (
                [long]$round.catalogRevision -ne [long]$previous.catalogRevision -or
                [long]$round.demandCount -ne [long]$previous.demandCount) {
                [void]$offenders.Add([string]$round.pollTraceId)
            }
        }

        $previous = $round
    }

    return [pscustomobject][ordered]@{
        Preserved = ($offenders.Count -eq 0)
        NonSuccessCount = $nonSuccessCount
        Offenders = @($offenders)
        Unverifiable = @($unverifiable)
    }
}

<#
.SYNOPSIS
  Verify the deployed package is byte-for-byte the artifact its release manifest records.
#>
function Test-PackageIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $PackageRoot,
        # Files the operator is expected to add on site. They are reported separately so
        # a filled local configuration does not read as a tampered package, and an
        # undeclared binary still does.
        [string[]] $OperatorSuppliedPaths = @(
            'service/appsettings.Local.json',
            'watch/appsettings.Local.json'),
        [string[]] $OperatorSuppliedPrefixes = @(
            'validation-runs/',
            'acceptance-runs/',
            'logs/')
    )

    $root = (Resolve-Path -LiteralPath $PackageRoot).Path.TrimEnd('\', '/')
    $manifestPath = Join-Path $root 'RELEASE-MANIFEST.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "RELEASE_MANIFEST_MISSING: $manifestPath"
    }
    try {
        $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    } catch {
        throw "RELEASE_MANIFEST_INVALID: $manifestPath"
    }

    $missing = New-Object System.Collections.ArrayList
    $mismatched = New-Object System.Collections.ArrayList
    $declared = New-Object System.Collections.Generic.HashSet[string] ([StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        [void]$declared.Add($relative)
        $full = Join-Path $root ($relative -replace '/', '\')
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            [void]$missing.Add($relative)
            continue
        }
        $file = Get-Item -LiteralPath $full
        $actual = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if ($file.Length -ne [long]$entry.length -or
            -not [string]::Equals($actual, [string]$entry.sha256, [StringComparison]::OrdinalIgnoreCase)) {
            [void]$mismatched.Add($relative)
        }
    }

    $unexpected = New-Object System.Collections.ArrayList
    $operatorSupplied = New-Object System.Collections.ArrayList
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse -Force)) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
        if ($relative -ieq 'RELEASE-MANIFEST.json') { continue }
        if ($declared.Contains($relative)) { continue }
        $isOperatorSupplied = ($OperatorSuppliedPaths -icontains $relative)
        if (-not $isOperatorSupplied) {
            foreach ($prefix in $OperatorSuppliedPrefixes) {
                if ($relative.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $isOperatorSupplied = $true
                    break
                }
            }
        }
        if ($isOperatorSupplied) {
            [void]$operatorSupplied.Add($relative)
        } else {
            [void]$unexpected.Add($relative)
        }
    }

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceDirty = $false
    $dirtyProperty = $manifest.PSObject.Properties['sourceDirty']
    if ($null -ne $dirtyProperty -and $null -ne $dirtyProperty.Value) {
        $sourceDirty = [bool]$dirtyProperty.Value
    }

    return [pscustomobject][ordered]@{
        Identical = ($missing.Count -eq 0 -and $mismatched.Count -eq 0 -and $unexpected.Count -eq 0)
        DeclaredFileCount = @($manifest.files).Count
        Missing = @($missing)
        Mismatched = @($mismatched)
        Unexpected = @($unexpected)
        OperatorSupplied = @($operatorSupplied)
        SourceCommit = [string]$manifest.sourceCommit
        SourceDirty = $sourceDirty
        ManifestSha256 = $manifestHash
    }
}

<#
.SYNOPSIS
  One acceptance result. A skip must name its owner, prerequisite and open release gate.
#>
function New-AcceptanceCheck {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Id,
        [Parameter(Mandatory = $true)][string] $Title,
        [Parameter(Mandatory = $true)][string] $Gate,
        [Parameter(Mandatory = $true)][ValidateSet('PASSED', 'FAILED', 'SKIPPED')][string] $Status,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Detail,
        [string] $Owner = '',
        [string] $Requires = '',
        [string[]] $Evidence = @()
    )

    if ($Id -cnotmatch '^[A-Z][A-Z0-9_]*$') {
        throw "ACCEPTANCE_CHECK_ID_INVALID: $Id"
    }
    if ($Status -ceq 'FAILED' -and [string]::IsNullOrWhiteSpace($Detail)) {
        throw "FAILED_CHECK_REQUIRES_DETAIL: $Id"
    }
    if ($Status -ceq 'SKIPPED' -and (
        [string]::IsNullOrWhiteSpace($Owner) -or [string]::IsNullOrWhiteSpace($Requires))) {
        throw "NAMED_SKIP_REQUIRES_OWNER_AND_PREREQUISITE: $Id"
    }
    if ([string]::IsNullOrWhiteSpace($Detail)) {
        throw "ACCEPTANCE_CHECK_REQUIRES_DETAIL: $Id"
    }

    return [pscustomobject][ordered]@{
        id = $Id
        title = $Title
        gate = $Gate
        status = $Status
        detail = Protect-FactoryAcceptanceText -Text $Detail
        owner = $Owner
        requires = $Requires
        evidence = @($Evidence)
    }
}

<#
.SYNOPSIS
  Assemble the reviewable acceptance summary from a declared checklist.

.DESCRIPTION
  The checklist is declared up front, so a check that never produced a result stops the
  summary instead of quietly shrinking it. Named skips are reported apart from passes and
  a run carrying one is never a plain pass.
#>
function New-FactoryAcceptanceSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Checks,
        [Parameter(Mandatory = $true)][string[]] $ExpectedCheckIds,
        [Parameter(Mandatory = $true)][string] $RollbackReadiness,
        [string[]] $ResidualRisks = @(),
        [hashtable] $Context = @{}
    )

    $seen = @{}
    foreach ($check in $Checks) {
        $id = [string]$check.id
        if ($seen.ContainsKey($id)) {
            throw "DUPLICATE_ACCEPTANCE_CHECK: $id"
        }
        $seen[$id] = $check
        if ($ExpectedCheckIds -cnotcontains $id) {
            throw "UNDECLARED_ACCEPTANCE_CHECK: $id"
        }
    }
    foreach ($expected in $ExpectedCheckIds) {
        if (-not $seen.ContainsKey($expected)) {
            throw "UNREPORTED_ACCEPTANCE_CHECK: $expected"
        }
    }

    $ordered = @($ExpectedCheckIds | ForEach-Object { $seen[$_] })
    $passed = @($ordered | Where-Object { $_.status -ceq 'PASSED' })
    $failed = @($ordered | Where-Object { $_.status -ceq 'FAILED' })
    $skipped = @($ordered | Where-Object { $_.status -ceq 'SKIPPED' })

    $status = if ($failed.Count -gt 0) {
        'FAILED'
    } elseif ($skipped.Count -gt 0) {
        'PASSED_WITH_NAMED_SKIPS'
    } else {
        'PASSED'
    }

    return [ordered]@{
        status = $status
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        counts = [ordered]@{
            declared = $ExpectedCheckIds.Count
            passed = $passed.Count
            failed = $failed.Count
            namedSkips = $skipped.Count
        }
        passed = $passed
        failed = $failed
        namedSkips = $skipped
        openGates = @($failed + $skipped | ForEach-Object { $_.gate } | Sort-Object -Unique)
        residualRisks = @($ResidualRisks)
        rollbackReadiness = $RollbackReadiness
        context = $Context
    }
}

<#
.SYNOPSIS
  Close the checklist of a run that aborted outside a check.

.DESCRIPTION
  A run can die between checks — a Host that will not start, a target that goes away.
  The remaining checks did not pass and did not earn a named skip either, so they are
  recorded as red with the abort reason. That keeps the gate closed and says why.
#>
function Complete-AbortedAcceptanceChecks {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]] $Checks,
        [Parameter(Mandatory = $true)][string[]] $ExpectedCheckIds,
        [Parameter(Mandatory = $true)][string] $Reason
    )

    if ([string]::IsNullOrWhiteSpace($Reason)) {
        throw 'ABORTED_RUN_REQUIRES_A_REASON'
    }
    $reported = @($Checks | ForEach-Object { [string]$_.id })
    $completed = New-Object System.Collections.ArrayList
    foreach ($check in $Checks) { [void]$completed.Add($check) }
    foreach ($expected in $ExpectedCheckIds) {
        if ($reported -ccontains $expected) { continue }
        [void]$completed.Add((New-AcceptanceCheck `
            -Id $expected `
            -Title 'Not reached' `
            -Gate 'FACTORY_RUN_ABORTED' `
            -Status FAILED `
            -Detail "Not reached: the acceptance run aborted before this check. $Reason"))
    }
    return @($completed)
}

<#
.SYNOPSIS
  Hash every evidence file so the returned bundle can be checked for tampering.
#>
function Get-EvidenceHashIndex {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    $root = (Resolve-Path -LiteralPath $Path).Path.TrimEnd('\', '/')
    return @(
        Get-ChildItem -LiteralPath $root -File -Recurse -Force |
            ForEach-Object {
                [ordered]@{
                    path = $_.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
                    length = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            } |
            Sort-Object { $_.path }
    )
}
