# MesIngest (C#)

MES task ingest Host. One surface, one contract: Oracle `MES_TASK_UNION` → causal round /
PollTrace → the dedicated SQL Server projection → the frozen, read-only `/api/v2/*` contract.

Outside `Development`, Host requires `MesIngest:NewSqlServerConnectionString` and
`MesIngest:SnapshotSource=Oracle`. The replaced contract is gone, not disabled: there is no
`/api/demands`, `/api/alerts`, `/api/poll-health`, `/api/demand-changes`, or `/openapi/v1.json`
to serve, no old schema to upgrade, and no configuration key that would re-enable them. A
configuration file that still carries a retired key fails startup instead of being ignored
(`MesIngestHostOptions.RetiredConfigurationKeys`). `/openapi/v2.json` is the only published API
description.

## Ticket 15 — Production Oracle round source

Production V2 runs the one approved `MES_TASK_UNION` statement from `service/queries/mes-task-union/query.sql`. Its raw-byte SHA-256 is compiled into Host and repeated in the adjacent query manifest and release manifest. A missing, empty, moved, duplicated, or modified SQL artifact is rejected before Oracle is called. Each successful read is one command / one result set covering all six branches and becomes one `MesTaskUnionRound` with PollTraceId, canonical query version, normalized content digest, row count, and outcome. The Oracle operation is read-only and bounded by `QueryTimeoutSeconds`; timeout, cancellation, execution, or result-shape failures produce safe failure evidence and never project partial rows.

Default **Thin** uses managed `Oracle.ManagedDataAccess.Core`. **Thick** is a separate Oracle ODBC/OCI adapter and requires both `OracleInstantClientDir` and the registered `OracleThickOdbcDriver`. Selecting Thick never falls back to Thin, and a bad mode or incomplete Thick configuration fails closed. Credentials stay in `appsettings.Local.json` / environment variables—never commit them.

```powershell
Copy-Item MesIngest.Host\appsettings.Local.json.example MesIngest.Host\appsettings.Local.json
# edit NewSqlServerConnectionString, OracleUser, OraclePassword, and OracleDataSource
# keep SnapshotSource=Oracle; Thin is the default production attempt
# for Thick also set OracleInstantClientDir and OracleThickOdbcDriver

dotnet run --project MesIngest.Host -- --probe-oracle
# exit 0 = attested live query success; 2 = live round failure; 3 = NOT_EXECUTED
# Output records requested/actual mode, driver, query version/hash, row count, duration,
# and stable diagnostic code without SQL, credentials, datasource, or raw MES values.
```

`PASSED` is valid only when the concrete runtime attempted a live Oracle connection, requested mode equals actual mode, the canonical query identity matches, and the round outcome is `Success`. Offline artifact checks and fake/CI sources are `NOT_EXECUTED`; they cannot certify factory connectivity. Try Thin first. Switch explicitly to Thick only when Thin fails and the plant's Instant Client plus registered Oracle ODBC driver are available.

Continuous production V2 (after the live probe succeeds):

```powershell
$env:MesIngest__NewSqlServerConnectionString = "Server=<SQL_HOST>;Database=MesIngestV2;..."
$env:MesIngest__SnapshotSource = "Oracle"
# appsettings.Local.json supplies Oracle credentials; ContinuousPollEnabled=true by default
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088
```

The SQL connection must target a dedicated empty database (which V2 bootstraps) or a database already matching the exact V2 schema contract. It is never an in-place migration target for the replaced schema, and Production has no in-memory fallback.

## Bounded history cleanup

The Host owns one cleanup loop; SQL Server Agent and Windows Task Scheduler do not delete
MesIngest business history. Checks use Host `TimeProvider` on fixed hourly boundaries and do
not catch up missed slots. The measured 600 rows per 14-second intake baseline freezes a
210,000-row hourly budget (more than 30% headroom). A real-SQL cleanup probe freezes the
separate safety caps at 25 whole Series and 15 elapsed seconds per check. Raw deletion is
further split into transactions of at most 25,000 rows / 50 PollTraces; successful poll
ingestion and the SQL schema both enforce the same 25,000-row PollTrace ceiling, so a raw
multiset is never partially deleted. A started Series cleanup always finishes
its tombstone and detailed graph in the existing indivisible transaction.

Operators may override `HistoryCleanupCheckIntervalSeconds`,
`HistoryCleanupMaximumRawObservationRowsPerBatch`,
`HistoryCleanupMaximumSeriesPerBatch`, and `HistoryCleanupTimeBudgetSeconds`; startup rejects
zero, negative, or unbounded values. These settings only control work per check and never
change either exact 15×24-hour retention window.

`GET /api/v2/current-ingest-attention` returns the durable `historyCleanup` status with the
last attempt/success, per-run and cumulative delete counts, earliest available Host UTC,
sanitized failure reason, and next check. A failure also appears as
`HISTORY_CLEANUP_FAILURE`; the next successful check clears that item. Cleanup failure alone
does not pause MES polling—storage-pressure pause remains a separate threshold policy.

Representative read-only endpoints are:

- `GET /api/v2/contract`
- `GET /api/v2/demand-series?presence=VISIBLE&page=1&pageSize=100`
- `GET /api/v2/current-ingest-attention?pageNumber=1&pageSize=100`
- `GET /api/v2/externally-readable-demand-catalog`
- `GET /api/v2/sublot-box-count?sublot=Q26067601-2`
- `GET /api/v2/poll-traces/{pollTraceId}`

`GET /api/v2/contract` returns the one exact, comparable contract identity and its stable capability set. Host and consumers must match that identity exactly; missing fields, old states, or client-side single-page filtering are not compatibility fallbacks. The canonical `/openapi/v2.json` describes only the read-only V2 GET surface, including its snapshot identities, ProjectionCommit, CatalogRevision, pagination bounds, stable ordering, conditional catalog reads, and error responses. No earlier OpenAPI document is served or supported.

`GET /api/v2/sublot-box-count` executes only the SHA-256-pinned
`queries/sublot-box-count/query.sql` with one exact bound `sublot`. It returns one fresh
`SUBLOT_BOX_COUNT` identity and positive `maxBoxCount`; missing, non-positive, malformed,
unavailable, or timed-out results fail closed. It never participates in the six-work-type poll
and never performs an Oracle write.

## Ticket 19–22 — production Watch client

`MesIngest.Watch` is a single-Host, business-read-only WPF operations client for the `/api/v2`
contract. It never hosts the poll loop and never reads SQL Server.

```powershell
# Terminal A — Host
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088

# Terminal B — Watch
dotnet run --project MesIngest.Watch
# optional overrides:
# $env:MesIngestWatch__BaseUrl = "http://127.0.0.1:5088"
# $env:MesIngestWatch__RenderingMode = "SoftwareOnly" # default; use Auto to restore WPF default rendering
# $env:MesIngestWatch__RequestTimeoutSeconds = "30"
```

Its information architecture is 概览 / 需求系列 / 资格审计 / 错误检索 / AREA 筛选 / 接入告警, plus
Host status and settings in the navigation footer. Every data view auto-refreshes; settings only
change the interval. A refresh replaces a view atomically on success, keeps the last successful
snapshot on loading or failure, and drops a late response that no longer matches the Host session,
the page query, and the request generation. Applying a new Host or credential raises the session
generation, cancels outstanding requests, clears every business snapshot and selection, and then
verifies the contract; a failed handshake never shows the previous Host's data.

Watch persists only approved local preferences: Host base URL, the external credential reference,
request timeout, refresh intervals, window geometry, and the named AREA filter profiles under the
current Windows user. Credentials remain in `MesIngestWatch__SharedSecret` or
`appsettings.Local.json`; business lists, queries, cursors, pages, selection, details, and Host
errors are never written to preference files. Corrupt, out-of-range, or incompatible preference
files fall back to safe defaults.

## Ticket 24 — release scripts, smoke, and validation guidance on the frozen V2 contract

Packaging, install, smoke, Watch acceptance, factory validation, and the return checklist all speak the frozen `/api/v2` contract. `pack/Test-ReleasePackage.ps1` additionally scans the packaged documentation and configuration for retired endpoints, legacy-only configuration keys, and old-contract explanations; a mention is allowed only on a line that says on the same line that the surface is retired.

`pack/validation/Invoke-ReleaseSmoke.ps1` drives rounds from a recording it writes itself, so a release smoke is repeatable with no factory Oracle. The recording replaces only the Oracle statement result — the canonical query artifact, `OracleMesTaskUnionRoundSource`, and the ProjectionCommit boundary are the shipped production code. Recorded rounds report driver `FILE_REPLAY` and `liveOracleAttested=false`, `--probe-oracle` refuses to run while a recording is configured, and `MesIngest:ReplayRoundsFromRecordingPath` needs `MesIngest:ReplayRoundsAcknowledgement=RELEASE_SMOKE_NOT_FACTORY_EVIDENCE` before the Host will start.

Beyond contract identity and the retired-surface 404s, the smoke proves the first catalog body with its `CatalogRevision`/weak ETag, a same-revision 304, restricted raw evidence denying a missing or wrong Bearer secret on localhost, a remote binding without a shared secret refusing to start, `pollTraceHighWater` advancing with no Watch process, and the SQL Server projection surviving an abrupt Host restart unchanged. `-IncludePackagedWatch` additionally starts and closes the packaged Watch on the interactive golden desktop; without it that check is recorded as the named skip `PACKAGED_WATCH_PROCESS_INDEPENDENCE_AND_STARTUP_BUDGET`.

`Invoke-PackagedReleaseGate.ps1` runs the packaged Watch through the non-pixel suites only (`watch-vm-tests` plus `watch-ui-journeys`). It runs on the golden desktop itself; until 2026-09-02 the same gate was one suite of `Invoke-GoldenRendererValidation.ps1`, which reached that desktop over PowerShell Direct from a control host. A packaged release proves that the published binaries start, connect, and drive the key journeys; it does not repeat the pixel candidates, stability counts, baseline promotions, or DPI clone that ticket 23 already accepted. Only packaging that actually changes PNG/XML/UIA/DPI output invalidates the affected ticket 23 scenarios, and only those gates are rerun.

## Ticket 25 — retired contract removed, empty-database cutover package

The replaced contract is deleted, not switched off. `FrozenMesFieldSet` / `FIELD_DRIFT` /
`REAPPEAR_AFTER_GONE`, the `IngestAlert` incident lifecycle, `DemandChangeFeed`, feed
sequence, bootstrap high-watermark, `SYNC_CURSOR_EXPIRED`, the old DTOs and routes, the old
Watch shell and its baselines, and the old schema upgrade path are gone from the domain,
Host, Watch, OpenAPI, tests, and documentation. `RetiredContractAndCutoverSafetyTests`
asserts the shipped assemblies define no type named for them.

Configuration follows: `MesIngest` refuses to start when the bound configuration still
carries a retired key (`SqlServerConnectionString`, `SnapshotCsvPath`,
`ChangeFeedRetentionHours`, `AlertRetentionDays`, `DisappearThreshold`, `GoLiveBaseline`,
`EnableLegacyDevelopmentEndpoints`) rather than ignoring it, so a deployment file written
for the old contract cannot look accepted while the value it carries does nothing. The
release smoke proves that refusal from the published binaries.

Database deletion exists in exactly one place: `pack/cutover/Invoke-EmptyDatabaseCutover.ps1`,
shipped as `scripts/cutover/`. Host, Watch, install, and uninstall never delete a database.
The drill connects through `master`, resolves the real `MachineName\InstanceName` and
database name from the connection, and requires the operator to type that identity back at
the console. There is no bypass switch, so a redirected or unattended run stops at the
confirmation instead of dropping an unconfirmed database. It then takes a full
`WITH CHECKSUM` backup, `RESTORE VERIFYONLY`s it, records its SHA-256, drops the old
database, recreates an empty one, and proves zero user tables. The new Host bootstraps the
schema on first start; the first complete SUCCESS round is the earliest provable point of
the new history.

`pack/cutover/Invoke-CutoverRollback.ps1` restores the previous deployment as a whole: the
new Service must be stopped, the old programs and configuration must already be back, and
the restored database must not carry the current `mesingest` schema. New binaries never read
the old database and old binaries never read the new one; there is no rolling or mixed mode.

`pack/Test-ReleasePackage.ps1` fails the package if any script other than the attended drill
can `DROP DATABASE`, if either drill loses its typed confirmation, or if a drill gains an
unattended bypass switch. See `pack/UPGRADE.md` for the runbook.

## Ticket 10 — factory install package + secure config

Self-contained install directory for plant copy-deploy:

```powershell
cd mes/ingest/csharp
.\pack\Publish-MesIngest.ps1 -OutputDir .\dist\MesIngest
# optional: -SkipWatch
```

Output: `service/` (Host plus the only formal query under `service/queries/`), optional `watch/` (the V2 Watch client), `templates/` (blank Local.json), `scripts/`, validation material, and release metadata. Copy the folder to the plant PC; copy `templates/appsettings.Local.json.example` to `service/appsettings.Local.json`, fill `NewSqlServerConnectionString`, keep `SnapshotSource=Oracle`, and add Oracle secrets locally (never commit or return the filled file).

Default HTTP bind is `http://127.0.0.1:5088`. If `MesIngest:Urls` binds beyond localhost, set `MesIngest:SharedSecret` and call with `Authorization: Bearer <secret>` (Watch: `MesIngestWatch__SharedSecret`). See `pack/INSTALL.md` for Windows Service install/start/stop/uninstall, logs, version, and troubleshooting.

## Ticket 11 — factory validation pack + feedback loop

Install package also ships `FACTORY-VALIDATION.md` and `validation/` (manifest / execution-log / return checklist / signoff templates).

`validation/Invoke-FactoryValidation.ps1` automates Ticket 15 read-only evidence capture per logical site A/B/C. It samples `/api/v2/contract`, snapshot-bound DemandSeries pages, DemandId/readability evidence, CurrentIngestAttention, ExternallyReadableDemandCatalog, and multiple distinct PollTraces; records request duration and correlation ids; exports DATES samples; redacts output; and creates a SHA-256 inventory. It accepts SharedSecret only through a named environment variable, never a command-line value.

Plant flow: fill V2 SQL Server and Oracle configuration → Thin `--probe-oracle` → on failure explicitly configure Thick and retry → start Service → sample multiple distinct `/api/v2/poll-traces/{pollTraceId}` rounds → compare row counts and VISIBLE projection → confirm PollTrace high-water continues without any Watch process → return only the redacted bundle.

A technically complete capture requires at least one imported real `LIVE_ORACLE` probe with `connection_attempted=true`, matching requested/actual mode, canonical query identity, `outcome=Success`, and `result=PASSED`; it also requires PollTrace query version/hash-derived identity, normalized content digest, row count, and outcome. Swagger/OpenAPI output and `ORACLE_QUERY` / `SQL_QUERY` / `SQL_WRITE` latency labels are not formal-source evidence. Human DATES/STEP interpretation and plant signoff remain separate from “validation pack ready”.

Repo import: copy to `mes/evidence/runs/<run_id>/` per `mes/experiments/definitions/mes-ingest-factory-validation/plan.md`. Do **not** use `meslab import-run` or promote to `samples/`. “验证包已就绪” ≠ “工厂已签字通过”.

## Tests


```powershell
dotnet test
```

Formal V2 seams include `IMesTaskUnionRoundSource`, `MesTaskUnionPollRunner`, `RoundIngestor`, and the read-only HTTP API. Real SQL Server Ticket 15 evidence runs with `Invoke-Ticket15SqlServerGate.ps1`; Oracle executor/probe CI tests use controlled fakes and never claim a plant Oracle pass.

Ticket 16's ProjectionCommit atomicity/concurrency release evidence must run against an explicitly approved real SQL Server. LocalDB and in-memory results are development feedback only and are insufficient release evidence. The gate writes its TRX plus JSON and Markdown reports under `.artifacts/ticket16-tests/`:

```powershell
$env:MES_INGEST_TICKET01_SQLSERVER = 'Server=<SQL_HOST>;Database=master;...'
.\Invoke-Ticket16SqlServerGate.ps1 -ExpectedProductMajor <major> -ExpectedCompatibilityLevel <level>
```
