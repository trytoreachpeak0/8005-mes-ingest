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

Roughly 6 minutes, and no VM. Run it once before handing off a completed
production-code change; do not repeat it unless production code, tests, or build
inputs changed after that run. Documentation and agent-configuration changes do
not need it.

Most of those 6 minutes are three tests. `ScaleAndQueryEvidenceGateTests` is 71%
of the suite's measured time (314 s of 441 s, 2026-09-02) because three of its
`[Fact]`s loop over a table of fixture mutations — 59, 14 and 17 cases — and each
case launches a fresh `pwsh` to run a 5,486-line validation script.
90 process launches, all independent, all serial. Nobody has fixed it; if the
suite's runtime starts to matter, that is where it is.

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

## What CI runs, and on which push

Four workflows, all on `win11-01`. Which one fires is decided by what a push
touches, and the partition is not arbitrary — it exists because both runners live
on the same 20-vCPU guest, so a pointless full suite competes with golden-renderer
work whose timing is load-bearing.

**All four share `concurrency: group: win11-01` with `cancel-in-progress: false`,
and that is not tidiness.** The group used to be per-workflow, so a single push
touching `MesIngest.Watch/**` started `test.yml` and `desktop-tests.yml` at the
same second on the same guest. Two runners on one VM are one machine, not two,
and golden-renderer timing is load-bearing.

Do not use `watch-vm-tests` timing as the evidence for this. It was, on
2026-09-02, and the attribution was wrong: that suite took 64 s alone and 94 s
when appended to `desktop-tests.yml`, which looked like contention until it took
91 s and failed again with the guest to itself. What actually slows it is state
the preceding step in the same job leaves behind, still undiagnosed, and the fix
was to move it off that job entirely rather than to change the group. The group
is right for its own reason; it just never proved this.

One caveat with the shared group: **three or more queued jobs do not queue, they
cancel.** GitHub keeps one pending run per group, so a new pending run cancels
the older pending one — observed with `repo-scan` running and `test` then
`desktop-tests` arriving behind it, which ended with `test` `cancelled`. Not yet
solved. A `cancelled` conclusion on a workflow that never started is this, not a
test failure.

`cancel-in-progress` must stay `false` in every one of them. A `true` anywhere in
a shared group lets an ordinary push cancel a running golden-renderer job
mid-capture, which leaves windows and processes on the desktop for the next run
to inherit.

This still guarantees nothing across repositories on its own — GitHub's
concurrency is per-repository, and `win11-01` also hosts runners for `riot-sdk`,
the control server and the protocol. **That part is a machine-wide named mutex's
job, and `Invoke-WithDesktopLock.ps1` owns its name.**

Two things to know before touching it:

- **Both desktop paths take it as of 2026-09-03, and one of them did not
  before.** `golden-renderer.yml` always had a mutex via
  `Invoke-WatchUiTests.ps1`, but under the repository-scoped name
  `Global\MesIngestWatchUiTests`, which serialised that script against itself and
  nothing else. `desktop-tests.yml` took no machine-wide lock at all — it ran
  `dotnet test` directly, and the only thing keeping it off the golden desktop
  was the shared `concurrency` group. Both comments nonetheless claimed
  cross-repository exclusion was "the desktop mutex's job". The first desktop job
  in any other repository would have walked straight through `desktop-tests.yml`.
- **The name is the contract, not the code.** Repositories here are independent
  clones with no shared package, so a second repository that needs the lock gets
  its own copy of the helper. What must match is
  `Global\W2G-InteractiveDesktop`. `Invoke-WatchUiTests.ps1` keeps its own
  acquire/release — it also guards direct manual invocation, which no workflow
  wrapper covers — but reads the name from the helper with `-NameOnly` rather
  than spelling it again.

Acquisition is fail-fast (`-TimeoutSeconds 0`, exit 3). That is right while this
repository is the only holder: contention can then only mean a human is running a
desktop suite on the guest, and failing loudly beats waiting silently. **A second
repository joining needs a real timeout instead** — a CI job must queue, not turn
red because it collided with another repository's schedule.

| Workflow | Runner | Fires on |
| --- | --- | --- |
| `test.yml` | `headless` | any push **except** `.github/**`, `docs/**`, `.claude/**`, root `*.md` |
| `repo-scan.yml` | `headless` | `docs/**`, `.claude/**`, root `*.md` |
| `desktop-tests.yml` | `golden-renderer` | `MesIngest.Watch/**`, `MesIngest.Tests/Watch*`, `**/*.xaml` |
| `golden-renderer.yml` | `golden-renderer` | nightly 03:00 (`watch-vm-tests`, then the pixel `verify`), and manual dispatch |

**A documentation change is not exempt from testing, it is routed.** Several
tests read the repository as data — `RetiredContractAndCutoverSafetyTests` walks
every file outside the build directories, and its credential scan reads every
`.md`, because operator documentation is exactly where a password gets pasted.
`repo-scan.yml` runs those and only those: 25 tests, 18 s, instead of 6 minutes.
`.github/**` is genuinely exempt: `.yml` is in neither extension list those scans
use, and no test opens a workflow file.

**Never add a path to `test.yml`'s `paths-ignore` without checking what reads
it.** `pack/**`, `queries/**`, every root `.ps1`, `appsettings*.json` and
`.gitignore` are all read by tests as data. "It is only a script" and "it is only
a doc" are both wrong here.

Two filters are derived from the source rather than written down, and for the
same reason: a hand-written list is a list someone forgets to update.
`Get-WpfDesktopTestFilter.ps1` partitions the desktop classes;
`Get-RepositoryScanTestFilter.ps1` finds the repository-walking ones. The second
matters more than it looks — a forgotten desktop class fails loudly on the wrong
runner, while a forgotten scanner keeps passing and simply stops running on the
changes it exists to check.

## Golden WPF renderer

Changes to `MesIngest.Watch` UI, XAML, Wpf.Ui controls, layout, UI Automation,
DPI behaviour or visual baselines run through the golden-renderer process. It is
**not** a higher tier of testing — it is a visual approval process whose output
is images for the user to judge. Read `docs/agents/golden-renderer.md` and
`docs/agents/fluent-ui.md` before implementing or validating such a change.

| Stage | Command | When |
| --- | --- | --- |
| Preview | `gh workflow run golden-renderer.yml -f mode=verify` | The change touches `MesIngest.Watch` UI. Under ten minutes; proves the baselines still reproduce (`0 received`). |
| Validation | `gh workflow run golden-renderer.yml -f mode=candidates`, `Invoke-PackagedReleaseGate.ps1` | **Only when cutting a release candidate, or on an explicit request to promote a visual baseline.** Tens of minutes, and `candidates` produces images someone must judge. |

- **Never enter Validation on your own initiative.** Say what it costs and what
  it proves, then ask. `verify` is different — it reads a number and changes
  nothing, and it already runs nightly on its own.
- Both run on the `golden-renderer` runner in session 1 of `win11-01`. `ssh vm01`
  lands in session 0, where WPF cannot render and the suite refuses to start.
- **`verify` runs nightly at 03:00, and is deliberately not on the commit path.**
  A pixel comparison cannot tell an intentional UI change from a regression, so
  blocking pushes with it would make red the normal state. A red nightly means
  either the machine drifted (environment gate failed) or the baselines are stale
  (gate passed, `received > 0`); `docs/agents/golden-renderer.md` has both.
- The non-pixel half of `MesIngest.Watch.UiTests` (`watch-vm-tests`, 170 tests,
  64 s) covers the visual-equivalence predicate and the text mask, without which
  `verify` could stay green while checking nothing. It reads no baseline, so it
  has no false reds — which on 2026-09-02 made it a blocking gate in
  `desktop-tests.yml`. That lasted a day. **It now runs in the nightly**, in its
  own job ahead of the rendering, because in that job it failed 3 runs out of 4
  on a 15-second per-test budget while passing every time it ran alone. The
  trigger is state the preceding step leaves on the desktop, not load: one of
  the failures had the guest to itself. Nothing on the commit path runs
  `MesIngest.Watch.UiTests` any more; changing that project is checked at 03:00,
  not on push.
- Validation is deliberately narrow. Its one irreplaceable job is catching
  intermittent defects — the Ticket 23 antialiasing flip appeared in ~12% of
  runs, which 3 runs miss about a third of the time — and that job does not arise
  in day-to-day work. An ordinary UI ticket stops at a preview plus the user's
  approval.
- Approved baselines live in `MesIngest.Watch.UiTests/WindowBaselines/` and are
  copied to the build output by the csproj. Anything under `.artifacts/` is
  evidence, not a baseline.

**The golden renderer is `win11-01` on the factory server** (`ssh vm01`), as of
2026-09-02. It replaced `gpt_win11` on the control machine, which is retired from
this role. The calibration is unchanged and still load-bearing: 1920x1080 at
100% / 96 DPI, `zh-CN`, China Standard Time, light theme, no RDP or Enhanced
Session. `Test-GoldenRendererEnvironment.ps1` checks all of it before any pixel
is produced — the 2026-08-28 baselines were captured on a machine running UTC and
carry `+00:00` timestamps as a result, which is what that gate exists to prevent.

## Toolchain baseline

This repository is pinned to the workspace-wide .NET toolchain. The authority is
`8005-agv-program/docs/adr/cross/0056-dotnet-toolchain-baseline.md`.

| Item | Pinned value | Enforced by |
| --- | --- | --- |
| SDK | 8.0.424, `rollForward: disable` | `global.json` |
| Target framework | `net8.0` and `net8.0-windows` | per project |
| Test stack | xunit.v3 3.2.2, Microsoft.NET.Test.Sdk 18.8.1, xunit.runner.visualstudio 3.1.5 | `Directory.Packages.props` |
| Banned packages | xunit v2, NUnit, MSTest, coverlet.collector | `Directory.Build.targets` |

Unlike the other repositories this one legitimately carries two target
frameworks — the WPF projects and their tests are `net8.0-windows`, the service
side is `net8.0`. Do not "unify" that; it is not drift.

**The migration is done as of 2026-09-04.** `MesIngest.Tests` moved from xunit
2.4.2 to xunit.v3 3.2.2, `coverlet.collector` is gone, and all four enforcement
layers are installed. The suite came out identical on both sides of it —
`Failed: 0, Passed: 961, Skipped: 138, Total: 1099` — and the skip count matters
as much as the failures here: 138 before and after means the SQL Server tests are
still skipping for the documented reason and did not turn into a new silent gap.

Three things about those layers are worth knowing before you touch them.

**The second layer has no build-time teeth, and the ADR used to claim it did.**
An inline `Version=` under central package management is silently ignored, not
rejected: NuGet strips the metadata during project evaluation, so a project
asking for `Oracle.ManagedDataAccess.Core 23.6.0` still resolves the central
`23.9.0` with no NU1008, no warning, and an empty `%(PackageReference.Version)`
in every MSBuild target. `CentralPackageVersionOverrideEnabled=false` governs the
`VersionOverride` attribute, not `Version`. The version never actually drifts,
but whoever wrote the inline one is not told it was ignored. Only
`check-toolchain.ps1` catches it, by reading the csproj as text. **Do not write
an MSBuild target for this** — one was written and deleted after it passed every
case it existed to fail.

**The fourth layer does work, and it is verified.** A deliberate
`<PackageReference Include="xunit" />` produces `error W2G0056` and fails the
build, while `xunit.v3` in the same build is untouched: the guard matches item
identity exactly, and `xunit.v3` is not `xunit`.

**`MesIngest.Tests` carries four suppressions, and they are debt, not a waiver.**
`CS8631`/`CS8620` come from v3's new `ReadOnlySpan<T>` assert overloads binding
to `Assert.Equal(["a"], somethingReturningString?[])` at 42 sites — the
comparisons are correct and `string` does implement `IEquatable<string>`.
`xUnit1051` (201 sites) and `xUnit2031` (39 sites) are analyzer style advice;
the first would change what these tests cancel, which is not a toolchain
change's business. `xUnit3003` was fixed rather than suppressed — it was two
attributes and it costs source location on failure. Any new test project uses
xunit.v3 and inherits none of these.

Never raise a version in one repository alone. Change the ADR and every
repository together.

## Build artifacts stay out of git

`.gitignore` covers `.artifacts/`. History before 2026-09-02 contained ~885 MB of
committed build output and golden-renderer runs; it was purged when this
repository was split out. **Do not commit anything under `.artifacts/`, and do not
commit packaged runtime binaries.**

## Scripting baseline

PowerShell 7. Do not write Windows PowerShell 5.1 compatible code, do not add
version probes or fallbacks, and do not invoke `powershell.exe` — call `pwsh`.
Every new `.ps1` opens with `#Requires -Version 7`.

Every tracked `.ps1` carries that header as of 2026-09-03; the 31 scripts that
still declared `5.1` were migrated then, and the C# tests that launched them
now start `pwsh`. A script that reaches for a 7.5-only cmdlet parameter declares
`#Requires -Version 7.5` instead — twelve of them do, for the reason below.

**`ConvertFrom-Json` needs `-DateKind String` in every one of these scripts.**
Windows PowerShell 5.1 left an ISO 8601 string alone; PowerShell 7 turns it into
a `[DateTime]` and drops the offset, so the `[DateTimeOffset]::Parse([string]$x)`
these scripts are built on re-reads `00:00Z` as `00:00+08:00` and silently shifts
every evidence timestamp by eight hours. That is what broke seven
`ScaleAndQueryEvidenceGateTests` cases during the migration, all of them landing
on a bounds check far from the actual cause. `-DateKind String` restores the 5.1
reading exactly; it is a PowerShell 7.5 parameter, hence the header bump. Adding
a bare `ConvertFrom-Json` to any of these scripts reintroduces the bug.
`Invoke-RestMethod` has no such parameter — the three call sites read only
version strings, capability ids and a `[long]` counter, and must stay that way.

The one frozen exception: already-released MES ingest scripts under
`.artifacts/releases/` keep their `#Requires -Version 5.1` header. They shipped
as 5.1 artifacts. Never retrofit a released script in either direction.
