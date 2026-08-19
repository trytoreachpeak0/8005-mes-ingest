# MesIngest (C#)

MES task ingest Host. The repository contains two deliberately different surfaces:

- **Production V2 (Tickets 15–17):** Oracle `MES_TASK_UNION` → causal round / PollTrace → the dedicated new SQL Server projection → frozen, read-only `/api/v2/*` contract.
- **Legacy Development V1:** CSV or the old snapshot source → `TransportDemandReconciler` → `/api/*`. This remains useful for historical tests and local demos only; it is not a V2 compatibility surface.

Outside `Development`, Host requires `MesIngest:NewSqlServerConnectionString`. With that key set and `MesIngest:SnapshotSource=Oracle`, only the V2 production poller and V2 HTTP surface run. The legacy `/api/demands`, `/api/alerts`, `/api/poll-health`, `/api/demand-changes`, and `/openapi/v1.json` are absent in Production; `/openapi/v2.json` is the canonical production API description.

## Tickets 01–07 — legacy Development V1

The following CSV, in-memory, old SQL projection, poll-health, and change-feed examples are retained as historical Development workflows. They do not describe the Ticket 15 production configuration or its V2 evidence contract.

### Ticket 01 quick start (CSV + in-memory)

```powershell
cd mes/ingest/csharp

# Point at a recorded MES_TASK_UNION CSV. Factory samples often have DATES before
# the default go-live baseline (2026-08-01); override baseline for local demos.
$env:MesIngest__SnapshotCsvPath = (Resolve-Path ..\..\samples\mes-task-union\latest.csv).Path
$env:MesIngest__GoLiveBaseline = "2026-07-01T00:00:00+08:00"

dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088
```

Then:

- `GET http://127.0.0.1:5088/api/demands`
- `GET http://127.0.0.1:5088/api/demands/{demandId}`
- `GET http://127.0.0.1:5088/api/alerts`
- `GET http://127.0.0.1:5088/api/poll-health`
- `GET http://127.0.0.1:5088/api/demand-changes`
- Swagger UI: `http://127.0.0.1:5088/swagger` · OpenAPI JSON: `/openapi/v1.json` (pack ships `openapi/v1.json`)

### Ticket 05 — legacy SQL Server projection

Set `MesIngest:SqlServerConnectionString` (env or local config). When set, Host persists TransportDemands, task-type pauses, alerts, and latest poll health; a process restart still serves them via the same read-only HTTP. When empty, Host keeps the in-memory store (unit/contract tests and CSV demos).

```powershell
Copy-Item MesIngest.Host\appsettings.Local.json.example MesIngest.Host\appsettings.Local.json
# edit connection string; appsettings.Local.json is gitignored

$env:MesIngest__SnapshotCsvPath = (Resolve-Path ..\..\samples\mes-task-union\latest.csv).Path
$env:MesIngest__GoLiveBaseline = "2026-07-01T00:00:00+08:00"
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088
```

Or:

```powershell
$env:MesIngest__SqlServerConnectionString = "Server=(localdb)\MSSQLLocalDB;Database=MesIngest;Trusted_Connection=True;TrustServerCertificate=True"
```

SQL Server round-trip smokes run when LocalDB is available, or when `MES_INGEST_SQLSERVER` is set. Credentials never belong in the repo.

### Ticket 06 — legacy restart recovery barrier

After process start, the first *successful* full poll may create/refresh VISIBLE demands but does not increment disappear counts or mark GONE, and does not enter `PAUSED_ZERO_DROP` on a zero count (persisted last-healthy is kept when count is 0). From the second successful poll, normal disappear/GONE and zero-drop rules resume. Barrier-round per-type counts may *raise* (or seed) the persisted last-healthy baseline, but never demote it — so zero-drop protection survives service recycle. Failed/incomplete rounds do not consume the barrier.

### Ticket 07 — legacy continuous single-flight poll

Host runs as a Windows Service-capable process (`UseWindowsService`) with Kestrel read-only API. Continuous poll is single-flight: one round at a time, then wait `PostPollDelaySeconds` (default 10) before the next. `QueryTimeoutSeconds` (default 30) bounds each snapshot read. File CSV mode uses the same poll host — edit the CSV between rounds and `GET /api/demands` reflects the new projection. Failed/incomplete rounds append `POLL_FAILURE` / `POLL_INCOMPLETE` alerts without mutating presence. Closing WPF (or never opening it) does not stop the service or API.

```powershell
$env:MesIngest__SnapshotCsvPath = (Resolve-Path ..\..\samples\mes-task-union\latest.csv).Path
$env:MesIngest__GoLiveBaseline = "2026-07-01T00:00:00+08:00"
# ContinuousPollEnabled=true and PostPollDelaySeconds=10 come from appsettings.json
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088
```

One-shot startup remains available via `MesIngest__RunOneShotOnStartup=true` (and/or `ContinuousPollEnabled=false`) for short legacy demos and tests. Service install packaging is ticket 10.

## Ticket 15 — Production V2 Oracle round source

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

The SQL connection must target a dedicated empty database (which V2 bootstraps) or a database already matching the exact V2 schema contract. It is not an in-place migration target for the legacy V1 tables, and Production has no in-memory fallback.

Representative read-only endpoints are:

- `GET /api/v2/contract`
- `GET /api/v2/demand-series?presence=VISIBLE&page=1&pageSize=100`
- `GET /api/v2/current-ingest-attention?pageNumber=1&pageSize=100`
- `GET /api/v2/externally-readable-demand-catalog`
- `GET /api/v2/poll-traces/{pollTraceId}`

`GET /api/v2/contract` returns the one exact, comparable contract identity and its stable capability set. Host and consumers must match that identity exactly; missing fields, old states, or client-side single-page filtering are not compatibility fallbacks. The canonical `/openapi/v2.json` describes only the read-only V2 GET surface, including its snapshot identities, ProjectionCommit, CatalogRevision, pagination bounds, stable ordering, conditional catalog reads, and error responses. The legacy `/openapi/v1.json` remains Development-only and is never production evidence.

## Ticket 09 — legacy Development Watch

The current WPF client is an optional read-only client for the legacy Development V1 surface. It never hosts the poll loop and never reads SQL Server. The commands and behavior below are retained for historical V1 testing; they are not a Production V2 acceptance path.

```powershell
# Terminal A — Host (CSV demo)
$env:MesIngest__SnapshotCsvPath = (Resolve-Path ..\..\samples\mes-task-union\latest.csv).Path
$env:MesIngest__GoLiveBaseline = "2026-07-01T00:00:00+08:00"
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088

# Terminal B — Watch
dotnet run --project MesIngest.Watch
# optional overrides:
# $env:MesIngestWatch__BaseUrl = "http://127.0.0.1:5088"
# $env:MesIngestWatch__RenderingMode = "SoftwareOnly" # default; use Auto to restore WPF default rendering
# $env:MesIngestWatch__RequestTimeoutSeconds = "30"
```

Watch shows VISIBLE/GONE demands with filter/sort (TASK_TYPE, SUBLOT, status, DemandId, time window), active/resolved IngestAlerts with production code/severity/LastSeenAt filters, a bottom health status bar (Watch last success vs Host poll end, connection/stale, active alerts/paused counts, timezone), and current-condition banners (ERROR red / WARNING orange, min 5s hold, brief 已恢复). Demand and Alert views use independent fixed 100-row server windows with previous/next cursor navigation; refresh preserves the current successful page while the committed filter/sort query remains active. Overview, VISIBLE, GONE, and IngestAlert each have a persisted, default-off auto-refresh preference with 10/30/60/300-second intervals; only the visible view runs, and its interval restarts after each request terminates. AlertId, Code, Severity, FirstSeenAt, LastSeenAt, TASK_TYPE, SUBLOT, DemandId, and Message sort through the Host allow-list with AlertId as the stable tie-break; OccurrenceCount, IsActive, and ResolvedAt remain display-only. HTTP timeout defaults to 30s (`Watch:RequestTimeoutSeconds`, range 1–300; invalid values fail startup). Watch defaults to WPF software rendering (`Watch:RenderingMode=SoftwareOnly`) so virtual/remote display drivers cannot leave an undrawn client area; `Auto` restores the platform default. On fetch failure Watch keeps last successful data per endpoint, shows last-success/stale in the status bar, and appends local WatchConnectionEvent JSONL under `%LocalAppData%\MesIngest.Watch\logs\` (or `MesIngestWatch__LogDirectory`; first failure / 5-minute summary / recovery; not Host IngestAlerts).

Watch persists only approved local preferences: Host base URL, the external credential reference, request timeout, four auto-refresh settings, window size, and the Demand/detail split. “恢复默认布局” resets only window geometry. Credentials remain in `MesIngestWatch__SharedSecret` or `appsettings.Local.json`; business lists, queries, cursors, pages, selection, details, last-success business time, and Host errors are never written to preference files. Corrupt, out-of-range, or incompatible preference files fall back to safe defaults.

## Ticket 24 — release scripts, smoke, and validation guidance on the frozen V2 contract

Packaging, install, smoke, Watch acceptance, factory validation, and the return checklist all speak the frozen `/api/v2` contract. `pack/Test-ReleasePackage.ps1` additionally scans the packaged documentation and configuration for retired endpoints, legacy-only configuration keys, and old-contract explanations; a mention is allowed only on a line that says on the same line that the surface is retired.

`pack/validation/Invoke-ReleaseSmoke.ps1` drives rounds from a recording it writes itself, so a release smoke is repeatable with no factory Oracle. The recording replaces only the Oracle statement result — the canonical query artifact, `OracleMesTaskUnionRoundSource`, and the ProjectionCommit boundary are the shipped production code. Recorded rounds report driver `FILE_REPLAY` and `liveOracleAttested=false`, `--probe-oracle` refuses to run while a recording is configured, and `MesIngest:ReplayRoundsFromRecordingPath` needs `MesIngest:ReplayRoundsAcknowledgement=RELEASE_SMOKE_NOT_FACTORY_EVIDENCE` before the Host will start.

Beyond contract identity and the retired-surface 404s, the smoke proves the first catalog body with its `CatalogRevision`/weak ETag, a same-revision 304, restricted raw evidence denying a missing or wrong Bearer secret on localhost, a remote binding without a shared secret refusing to start, `pollTraceHighWater` advancing with no Watch process, and the SQL Server projection surviving an abrupt Host restart unchanged. `-IncludePackagedWatch` additionally starts and closes the packaged Watch on the interactive golden desktop; without it that check is recorded as the named skip `PACKAGED_WATCH_PROCESS_INDEPENDENCE_AND_STARTUP_BUDGET`.

`Invoke-GoldenRendererValidation.ps1 -Suite watch-package-release` runs the packaged Watch through the non-pixel suites only (`watch-vm-tests` plus `watch-ui-journeys`). A packaged release proves that the published binaries start, connect, and drive the key journeys; it does not repeat the pixel candidates, stability counts, baseline promotions, or DPI clone that ticket 23 already accepted. Only packaging that actually changes PNG/XML/UIA/DPI output invalidates the affected ticket 23 scenarios, and only those gates are rerun.

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

A technically complete capture requires at least one imported real `LIVE_ORACLE` probe with `connection_attempted=true`, matching requested/actual mode, canonical query identity, `outcome=Success`, and `result=PASSED`; it also requires PollTrace query version/hash-derived identity, normalized content digest, row count, and outcome. Legacy `/api/poll-health`, ChangeFeed/Bootstrap, Swagger/OpenAPI, and legacy `ORACLE_QUERY` / `SQL_QUERY` / `SQL_WRITE` latency labels are not V2 formal-source evidence. Human DATES/STEP interpretation and plant signoff remain separate from “validation pack ready”.

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
