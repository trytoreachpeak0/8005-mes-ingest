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

The production real-window journey launches `MesIngest.Watch.exe`, drives it through
FlaUI.UIA3 5.0.0, and serves the versioned V2 read contract from
`ScriptedFakeHost`. It never injects a page adapter into the Watch process. The
shared tickets 19-22 journey verifies the Fluent chrome and records real-window
previews for Overview, Settings, DemandSeries detail, Readability Audit detail,
AREA profiles, Error Search Variant A, CurrentIngestAttention, and the explicit
Series-error drill back to Error Search. Each run also retains the production UIA
tree, redacted V2 request timeline, Watch logs, stdout, and stderr.

Run UIA journey smoke at 100%, 125%, or 150% DPI:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite watch-ui-journeys
```

Run the implementation-train non-pixel suite and the production real-window
journey from one deployed payload, without invoking a baseline suite:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite watch-production-preview
```

The old five-state window matrix remains isolated under `watch-window-visual`
until the shared baseline ticket replaces it. Do not use that historical matrix
as approval evidence for the production workspace.

Run the five pixel-exact `1440x900` client-area baselines only on the calibrated 100%
environment:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite watch-window-visual
```

The single local release-gate entry runs all four suites in order and owns a named
desktop mutex, so neither another assembly nor another desktop driver can overlap it:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite all
```

On the first failure the entry exits non-zero without rerunning. It retains the runner
log and a redacted evidence directory containing expected/actual/diff when applicable,
received XAML when supplied by Verify, step/failure screenshots, UIA tree, Watch logs,
separate Watch stdout/stderr, fake Host timeline and summary, the failed step/exception/
timeout, and an environment manifest. A rerun is a separate maintainer action and is
diagnostic only; it never changes the original result.

See [`WindowBaselines/README.md`](WindowBaselines/README.md) for baseline review and
[`UI-GATE-POLICY.md`](UI-GATE-POLICY.md) for 50-run and flaky governance.
