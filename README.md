# MesIngest (C#)

Phase-1 MES task ingest host: snapshot → `TransportDemandReconciler` → projection store → read-only HTTP.

## Ticket 01 quick start (CSV + in-memory)

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

## Ticket 05 — SQL Server projection

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

## Ticket 06 — restart recovery barrier

After process start, the first *successful* full poll may create/refresh VISIBLE demands but does not increment disappear counts or mark GONE, and does not enter `PAUSED_ZERO_DROP` on a zero count (persisted last-healthy is kept when count is 0). From the second successful poll, normal disappear/GONE and zero-drop rules resume; barrier-round per-type counts are adopted as the recovery baseline for that second poll. Failed/incomplete rounds do not consume the barrier.

## Ticket 07 — Windows Service continuous single-flight poll

Host runs as a Windows Service-capable process (`UseWindowsService`) with Kestrel read-only API. Continuous poll is single-flight: one round at a time, then wait `PostPollDelaySeconds` (default 10) before the next. `QueryTimeoutSeconds` (default 30) bounds each snapshot read. File CSV mode uses the same poll host — edit the CSV between rounds and `GET /api/demands` reflects the new projection. Failed/incomplete rounds append `POLL_FAILURE` / `POLL_INCOMPLETE` alerts without mutating presence. Closing WPF (or never opening it) does not stop the service or API.

```powershell
$env:MesIngest__SnapshotCsvPath = (Resolve-Path ..\..\samples\mes-task-union\latest.csv).Path
$env:MesIngest__GoLiveBaseline = "2026-07-01T00:00:00+08:00"
# ContinuousPollEnabled=true and PostPollDelaySeconds=10 come from appsettings.json
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088
```

One-shot startup remains available via `MesIngest__RunOneShotOnStartup=true` (and/or `ContinuousPollEnabled=false`) for short demos and tests. Service install packaging is ticket 10.

## Ticket 08 — Oracle production source + probe

Production snapshot mode runs the official `MES_TASK_UNION` SQL from the published `queries/` folder (copied from `mes/queries/mes-task-union` at build/publish — no divergent SQL fork in the csharp tree). Driver is `Oracle.ManagedDataAccess.Core` (managed). Default mode is **Thin**; `OracleMode=Thick` prepends Instant Client (`OracleInstantClientDir` or `ORACLE_CLIENT_LIB_DIR`) onto `PATH` for plant 11g / TNS practice without changing business code — it does not switch to an unmanaged OCI driver. Credentials stay in `appsettings.Local.json` / env — never commit them. `QueryTimeoutSeconds` bounds both the poll CancelAfter and ODP.NET `CommandTimeout`.

```powershell
Copy-Item MesIngest.Host\appsettings.Local.json.example MesIngest.Host\appsettings.Local.json
# edit OracleUser / OraclePassword / OracleDataSource; use Thick + Instant Client on plant 11g if Thin fails

dotnet run --project MesIngest.Host -- --probe-oracle
# exit 0 = query success; non-zero = failure. Output has no password.
```

Continuous Oracle poll (after probe succeeds):

```powershell
$env:MesIngest__SnapshotSource = "Oracle"
# Local.json supplies credentials; ContinuousPollEnabled=true from appsettings.json
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088
```

This ticket ships factory-ready connectivity capability; it does **not** claim the plant link has already been verified (that is ticket 11).

## Ticket 09 — WPF watch thin client

WPF is an optional read-only HTTP client. It never hosts the poll loop and never reads SQL Server. Start the Host first, then open Watch against the same base URL (default `http://127.0.0.1:5088`). Closing Watch leaves the Windows Service / Host process polling and serving the API; start Watch again later to reconnect and show the current projection.

```powershell
# Terminal A — Host (CSV demo)
$env:MesIngest__SnapshotCsvPath = (Resolve-Path ..\..\samples\mes-task-union\latest.csv).Path
$env:MesIngest__GoLiveBaseline = "2026-07-01T00:00:00+08:00"
dotnet run --project MesIngest.Host --urls http://127.0.0.1:5088

# Terminal B — Watch
dotnet run --project MesIngest.Watch
# optional overrides:
# $env:MesIngestWatch__BaseUrl = "http://127.0.0.1:5088"
# $env:MesIngestWatch__RefreshSeconds = "2"
```

Watch shows VISIBLE/GONE demands with filter/sort (TASK_TYPE, SUBLOT, status, last seen), recent alerts, latest poll health, and prominent banners for query/poll failure and `PAUSED_ZERO_DROP`.

## Tests


```powershell
dotnet test
```

Formal seams: `TransportDemandReconciler`, read-only HTTP. Supporting: `SingleFlightPollLoop`, SQL Server store persistence smoke (env available), Oracle source/probe (fake executor; no CI plant Oracle).
