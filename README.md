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

## Tests

```powershell
dotnet test
```

Formal seams: `TransportDemandReconciler`, read-only HTTP. Supporting: `SingleFlightPollLoop`, SQL Server store persistence smoke (env available).
