# MesIngest Watch UI tests

This is the independent xUnit v3 test host for the production WPF Watch application.
Tests create the real `WatchApplicationComposition` and replace the Host boundary with
`ScriptedFakeHost`, so production Host-session generation, cancellation, browse, and
atomic UI commit behavior stay in the path under test. Tests that assert time-dependent
rendering may also inject a deterministic `TimeProvider`; no product state or projection
boundary is replaced.

Scenarios can repeat one response, return a deterministic response sequence, or select
a response from the incoming query. Cursor-expiry replies use the same HTTP 400 shape
that the production browse session recognizes for its single safe recovery.

Use fake values only. Request timelines intentionally record session ids, operation
names, states, and redacted endpoint shapes; they never record credentials, cursors,
Demand ids, or response payloads.

From `mes/ingest/csharp`, run the non-pixel suite in a logged-in Windows desktop:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite watch-vm-tests
```

If no interactive input desktop is available, the entry exits with code `2` and a
`WATCH_UI_ENVIRONMENT_UNAVAILABLE` message before running product tests.

The required Verify.Xaml 4.2.1 matrix is separate:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite watch-xaml-visual
```

Before starting the test process, this suite checks the active desktop, 1920x1080
resolution, 96 DPI, Windows light app theme, `zh-CN` culture and UI culture,
Microsoft YaHei UI and Consolas, and the default `SoftwareOnly` rendering mode. Any
difference exits with code `2` before Verify can write a received artifact. See
[`Baselines/README.md`](Baselines/README.md) for the 10-run stability and human-review
workflow. Tests and candidates contain fixed fake data only.
