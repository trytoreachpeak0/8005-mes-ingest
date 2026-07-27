# MesIngest (C#)

Phase-1 MES task ingest host: recorded CSV snapshot → `TransportDemandReconciler` → in-memory projection → read-only HTTP.

## Ticket 01 quick start

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

## Tests

```powershell
dotnet test
```

Formal seams: `TransportDemandReconciler`, read-only HTTP (`/api/demands`).
