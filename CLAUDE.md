# 8005-mes-ingest

The MesIngest service and its `MesIngest.Watch` WPF operator client. Split out of
`8005---AGV` on 2026-09-02; that repository is now `8005-agv-program` and holds
requirements, cross-subsystem ADRs and the decision archive.

## Write authority

This repository is writable. Elsewhere in the workspace:
`8005-agv-control-server` and `8005-agv-program` are writable;
`8005-agv-onboard-hmi` and `slots-simulator` are read-only for agents;
`8005-agv-protocol` is writable but every push there must be announced to Kun
Wang in an issue that `@SocialKKKK`.

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues. See `docs/agents/issue-tracker.md`.
External pull requests are treated as a request surface and run through the same
triage labels — that flag is on.

### Triage labels

The five canonical roles (`needs-triage`, `needs-info`, `ready-for-agent`,
`ready-for-human`, `wontfix`), created in this repository. See
`docs/agents/triage-labels.md`.

### Domain docs

`CONTEXT.md` at the repository root plus `docs/adr/`. See `docs/agents/domain.md`.

### Matt Pocock's skills

Installed as the `mattpocock-skills` plugin (user-level). Invoke them namespaced:
`/mattpocock-skills:<name>`. They are explicit-only — use one when the user names
it. `code-review` collides with the bundled `/code-review`; use
`/mattpocock-skills:code-review` for the Standards+Spec review.

### Deciding what to work on

The workspace ships a `w2g-next` skill. When the next step is unclear, it reads
the real state — working tree, the slice board issue, `integration-slices/index.json`,
open issues, gate evidence — and applies a fixed priority ladder to name one
action. Prefer it over guessing.

## Language

Agent instruction files — this one, `.claude/rules/`, `docs/agents/` — are
written in **English**.

Everything a human reads is written in **Chinese**: README files, documentation
prose, ADR bodies, evidence summaries, issue and pull-request titles and bodies,
and commit message bodies.

Stay English inside Chinese text: conventional commit prefixes (`feat:`, `fix:`,
`docs:`, `chore:`), identifiers, paths, commands, environment variables and error
codes. Quote an error or a test result in its original English first, then
explain it in Chinese. Do not rewrite existing text to match; this governs new
writing.

## Tests

**"Run the full test suite" means this command**, run from the repository root.
It never means the golden renderer:

```powershell
dotnet test MesIngest.Tests
```

Roughly 50 s of tests plus ~2.5 min of cold build, and no VM. Run it once before
handing off a completed production-code change; do not repeat it unless
production code, tests, or build inputs changed after that run. Documentation and
agent-configuration changes do not need it.

Test authorization is scoped to the current task. A request to inspect, tidy,
commit, or push an already-dirty worktree does **not** authorize a test run.
Never infer it from `git status`.

**Tier 1 silently skips the SQL Server tests** unless three environment variables
are set: `MES_INGEST_TICKET01_SQLSERVER` (a real instance — LocalDB is rejected
on purpose), `MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR`, and
`MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL`. Without them the run still
reports `Failed: 0` while skipping 88 tests, so a change to any SQL in
`SqlServerMesIngestProjection.*.cs` is not covered. **Check the skip count, not
just the failure count.**

## Golden WPF renderer

Changes to `MesIngest.Watch` UI, XAML, Wpf.Ui controls, layout, UI Automation,
DPI behaviour or visual baselines run through the golden-renderer process. It is
**not** a higher tier of testing — it is a visual approval process whose output
is images for the user to judge. Read `docs/agents/golden-renderer.md` and
`docs/agents/fluent-ui.md` before implementing or validating such a change.

| Stage | Command | When |
| --- | --- | --- |
| Preview | `.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite <one suite>` | The change touches `MesIngest.Watch` UI. Minutes, needs the interactive golden desktop. |
| Validation | `.\Invoke-GoldenRendererValidation.ps1`, `Test-*Stability.ps1 -Runs 3` | **Only when cutting a release candidate, or on an explicit request to promote a visual baseline.** Tens of minutes. |

- **Never enter either stage on your own initiative.** Say which suite, what it
  costs and what it proves, then ask.
- Validation is deliberately narrow. Its one irreplaceable job is catching
  intermittent defects — the Ticket 23 antialiasing flip appeared in ~12% of
  runs, which 3 runs miss about a third of the time — and that job does not arise
  in day-to-day work. An ordinary UI ticket stops at a preview plus the user's
  approval.
- Approved baselines live in `MesIngest.Watch.UiTests/WindowBaselines/` and are
  copied to the build output by the csproj. Anything under `.artifacts/` is
  evidence, not a baseline.

**The golden VM `gpt_win11` runs on the control machine's own Hyper-V.** That is
the one sanctioned exception to the workspace rule that experiments belong on the
factory server's `ssh vm01`; it still requires the user's authorization each
time, and its calibration (1920x1080 at 100% / 96 DPI, no RDP or Enhanced
Session) is load-bearing.

## Build artifacts stay out of git

`.gitignore` covers `.artifacts/`. History before 2026-09-02 contained ~885 MB of
committed build output and golden-renderer runs; it was purged when this
repository was split out. **Do not commit anything under `.artifacts/`, and do not
commit packaged runtime binaries.**

## Scripting baseline

PowerShell 7. Do not write Windows PowerShell 5.1 compatible code, do not add
version probes or fallbacks, and do not invoke `powershell.exe` — call `pwsh`.
Every new `.ps1` opens with `#Requires -Version 7`.

The one frozen exception: already-released MES ingest scripts under
`.artifacts/releases/` keep their `#Requires -Version 5.1` header. They shipped
as 5.1 artifacts. Never retrofit a released script in either direction.
