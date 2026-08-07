# MesIngest Watch UI tests

This is the independent xUnit v3 test host for the production WPF Watch application.
Tests create the real `WatchApplicationComposition` and replace only the Host boundary
with `ScriptedFakeHost`, so production Host-session generation, cancellation, browse,
and atomic UI commit behavior stay in the path under test.

Use fake values only. Request timelines intentionally record session ids, operation
names, states, and redacted endpoint shapes; they never record credentials, cursors,
Demand ids, or response payloads.

From `mes/ingest/csharp`, run the serial local entry in a logged-in Windows desktop:

```powershell
.\Invoke-WatchUiTests.ps1
```

If no interactive input desktop is available, the entry exits with code `2` and a
`WATCH_UI_ENVIRONMENT_UNAVAILABLE` message before running product tests.
