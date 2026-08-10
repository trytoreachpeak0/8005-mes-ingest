# Watch UI release-gate policy

`Invoke-WatchUiTests.ps1 -Suite all` is the only local release-gate entry. It runs
`watch-vm-tests`, `watch-xaml-visual`, `watch-ui-journeys`, and `watch-window-visual`
serially while holding the global `MesIngestWatchUiTests` desktop mutex.

The first failure remains a failed gate. Automatic retry is forbidden. A separate rerun
may diagnose the cause but cannot replace or erase the original evidence/result.

Before enabling this gate for a release branch, run
`Test-WatchUiGateStability.ps1 -Runs 50`; all 50 consecutive runs must pass. After a fix
or removal from quarantine, the same 50-pass requirement applies again.

A test is flaky when non-product failures occur at either threshold:

- 2 times in any 20 consecutive runs; or
- 3 times in any rolling 100 runs.

Quarantine requires an owner, a repair ticket, the first-failure evidence path, and a
deadline no later than 7 calendar days. Quarantine cannot silently turn failures into
passes; the release record must show that the named check was excluded. Global pixel
tolerance and broad masks are forbidden.
