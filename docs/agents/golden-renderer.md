# Golden WPF renderer

This runbook is the repository-wide validation contract for tickets that change
`MesIngest.Watch` UI, XAML, Wpf.Ui controls, visual layout, UI Automation, DPI
behavior, or screenshot/XAML baselines.

## Machine contract

| Item | Required value |
| --- | --- |
| Hyper-V VM | `win11-01`, on the factory server's Hyper-V |
| Guest host | `DESKTOP-F9HC40O` |
| Interactive user | `DESKTOP-F9HC40O\agvops`, session 1, autologon |
| Desktop | 1920x1080, 100% / 96 DPI |
| Locale / timezone | `zh-CN` / `China Standard Time` |
| Theme | Windows apps light theme |
| Fonts | Microsoft YaHei UI and Consolas |
| WPF rendering | `SoftwareOnly` |
| PowerShell | 7.x |
| .NET SDK | 8.0.424, pinned by `global.json` |
| NuGet | direct, from the guest |

`Test-GoldenRendererEnvironment.ps1` checks every row that is observable from
inside the process, and the workflow runs it before any pixel is produced. Trust
that report over this table: the timezone row in particular is load-bearing and
was silently violated once. `WatchTimeDisplay.Format` renders through
`TimeZoneInfo.Local`, so a machine on UTC bakes `+00:00` timestamps into the
baselines instead of `+08:00` — which is exactly how the 2026-08-28 baselines
were captured, on a machine the gate never checked.

The machine moved here from `gpt_win11` on the control machine on 2026-09-02.
Nothing about the *contract* changed; what changed is that the desktop now
belongs to a CI runner instead of a scheduled task driven over PowerShell
Direct, so the orchestration around it is gone.

## Connection rules

- The desktop is driven by the `golden-renderer`-labelled GitHub Actions runner,
  which runs in the guest's session 1 under a logon-triggered scheduled task.
  Everything below happens as a workflow step on that runner.
- `ssh vm01` reaches the guest, but lands in session 0. That session has no
  interactive window station: WPF fails there with dispatcher thread-affinity
  errors that name nothing resembling the real cause, and
  `Invoke-WatchUiTests.ps1` refuses to start. Use it for inspection, never to
  run a suite.
- Use Hyper-V Basic Session only when a human must inspect the real desktop.
  It runs on the factory server: `vmconnect.exe localhost win11-01`, or
  `remote-ops/factory-server/scripts/vm-screenshot.ps1` for a still.
- Do not use RDP or Hyper-V Enhanced Session. They can change desktop session,
  resolution, scaling, font rasterization, and screenshot output.
- Never run two desktop suites in parallel. Within this repository the
  `concurrency: desktop` group enforces that. It does **not** reach across
  repositories — GitHub's concurrency is per-repository, and `win11-01` carries
  runners for several — so the `Global\MesIngestWatchUiTests` mutex in
  `Invoke-WatchUiTests.ps1` remains the real guarantee.
- The guest also hosts `headless` runners for this and other repositories, and
  they are not covered by either mechanism. A full `dotnet test` running
  concurrently competes for the same 20 vCPU and 8 GB, which shifts render
  timing. Check that the machine is idle before a capture that will become a
  baseline.

## Standard entry points

These are the suites. On the runner they are workflow steps; at the guest's own
desktop they are what you type from the repository root:

```powershell
.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite watch-vm-tests
.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite watch-ui-journeys
.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite watch-production-preview
.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite watch-window-visual
.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite all
```

`watch-production-preview` runs `watch-vm-tests` followed by the production
`watch-ui-journeys` inside one desktop-mutex lease. It is the
implementation-train preview entry point: it does not run either baseline
comparison suite and does not create, promote, or approve a baseline.

For the baseline work, dispatch `.github/workflows/golden-renderer.yml`. It is
`workflow_dispatch`-only and takes two modes:

```bash
gh workflow run golden-renderer.yml --ref main -f mode=verify     -f runs=3
gh workflow run golden-renderer.yml --ref main -f mode=candidates -f runs=3
```

- `verify` drives `Test-WatchWindowBaselineStability.ps1 -Mode Promoted`: it
  renders the 11 windows and requires every one to reproduce the approved
  baseline. **0 received is the pass condition.**
- `candidates` drives `-Mode Candidate`: it renders 11 fresh candidates `runs`
  times and requires them to agree across every run, byte for byte or under the
  bounded predicate below.

Neither mode promotes anything, and neither can: the runner has no write access
to the repository. Candidates leave the machine as a build artifact, and turning
them into baselines is a commit a human makes.

## Packaged release gate

`Invoke-PackagedReleaseGate.ps1` is a different gate that happens to need the same
desktop. It builds the release package, installs it clean, and proves the
*published binaries* start, connect, and drive the key journeys — release smoke
with `-IncludePackagedWatch`, the `MesIngest.Tests` regression against a real SQL
Server with every skip named and approved, and `Invoke-WatchAcceptance.ps1`
driving the packaged Watch through the non-pixel suites. It does not repeat the
pixel work.

It is not in a workflow, and deliberately: it needs a DPAPI-protected credential
for a dedicated, disposable, empty SQL Server database, which is not a repository
secret. Run it on the golden desktop when cutting a release candidate.

Until 2026-09-02 this was `Invoke-GoldenRendererValidation.ps1 -Suite
watch-package-release`: 823 lines of which the majority staged a payload, opened a
`PSSession` to `gpt_win11`, registered an Interactive scheduled task, polled it,
copied evidence back and tore it down. The golden desktop is now a CI runner in
the same session, so that transport described a hop that no longer exists. The
gate itself is unchanged, including its two ordering invariants: no signoff and no
release zip until residual processes are gone and the environment gate passes a
second time. `InstallPackageLayoutTests` pins both, because the gate cannot run in
CI.

## Narrowing a suite while iterating

`Invoke-WatchUiTests.ps1` accepts `-Class` and `-Method`. Both pass through to the
xUnit v3 runner and restrict the selected suite to part of itself:

```powershell
.\Invoke-WatchUiTests.ps1 -Configuration Release -Suite watch-ui-journeys `
    -Method '*ErrorSearch*'
```

Use this while diagnosing one scenario, so re-checking a single capture does not
cost the whole category. Narrowing is rejected for `-Suite all` and
`-Suite watch-production-preview`, which expand to several suites.

A narrowed run proves nothing about the suite, and the script says so:

- it logs `WATCH_UI_PARTIAL_RUN` and finishes with `WATCH_UI_PARTIAL_PASSED`,
  never `WATCH_UI_ALL_PASSED`;
- it writes `<suite>.partial.runner.log`, not `<suite>.runner.log`;
- it does not clear existing `*.received.*` files, because it does not regenerate
  the scenarios it skips.

The environment gate below still runs. Every step in "Approval and baseline order"
requires an un-narrowed run.

## Required environment gate

`Test-GoldenRendererEnvironment.ps1` runs before the suite — as the workflow's
own step, so a drifted machine fails in the log rather than surfacing later as a
mysterious pixel difference. Formal 100% visual runs require all of the
following:

- Explorer and an input desktop in the same interactive session;
- 1920x1080 desktop and 96 DPI;
- light apps theme;
- `zh-CN` culture and UI culture;
- `China Standard Time`;
- Microsoft YaHei UI and Consolas;
- `MesIngestWatch__RenderingMode=SoftwareOnly`.

If any condition differs, stop before producing `received` files or new
baselines. Retain the environment report as red evidence.

## Baseline order

For visual changes, the order is mandatory:

1. Run code/non-pixel regressions.
2. Check the machine is idle, then dispatch `mode=candidates`. It generates a
   fresh candidate matrix; do not bulk-promote historical candidates.
3. That run repeats the matrix `-Runs` consecutive times (default 3) and requires
   every run to be byte-identical or visually equivalent under the bounded
   predicate below. A run that is not stable produces no baseline.
4. Show the candidate images to the repository owner. They decide; the images do
   not block the machine work, so send them and continue.
5. Promote the reference run's candidates — the run the stability manifest was
   built from, `run-01`. Copy `*.candidate.png` and `*.candidate.text-mask.json`
   to `*.verified.png` and `*.verified.text-mask.json`; both files, always
   together. A promoted PNG whose mask stayed behind fails the next comparison
   on mask growth rather than on pixels, which reads as a rendering problem and
   is not one.
6. Commit the promotion with the run id, the stability result, and the reason the
   old baselines were invalid.
7. Dispatch `mode=verify` and require `0 received`.

**This repository has one owner, and that is the whole approval process.** There
is no proposal record, no second reviewer, and no `approvalState` field; a script
that manufactured those existed until 2026-09-02 and was deleted because it could
only ever be filled in with a reviewer who does not exist. What replaces it is
step 6: the commit message is the record, and it must say enough that the next
person can tell an intentional change from a drifted machine.

A later UI change invalidates an earlier visual approval — and that is not
hypothetical. The baselines promoted on 2026-08-28 were invalidated the next day
by `614cc6a`, which deliberately replaced the bilingual UI labels with Chinese
ones, and nobody re-ran the renderer for five days. Nothing detects this on its
own: `verify` is manual, and until someone dispatches it the repository holds
baselines that match no machine's output. Re-run it after any commit that touches
`MesIngest.Watch` UI.

Test-only normalization may reuse approval only when all approved PNG SHA-256
hashes remain identical; record that comparison in evidence.

Never hide a red run with a successful rerun. Keep the first failure, explain
the cause, add a regression test when possible, and restart the required
consecutive-run count from one.

## Visual equivalence

Byte equality is the fast path and the default. It is not, however, the definition
of "unchanged": WPF may rasterise an identical glyph run into slightly different
antialiasing intensities between processes, leaving the glyph's pixel support
untouched and shifting individual grey levels by one or two. That difference carries
no visual information, and failing on it makes the gate reject its own renderer
rather than a regression.

### Text pixels

Text content is not a pixel-baseline concern. At each production baseline capture the
journey walks the live UI Automation control view, collects every `Text` element's
physical client-area rectangle, expands it by 2 pixels for glyph antialiasing, clips it
to the frame, and writes `<step>.text-mask.json`. Candidate captures also write the
mask beside the PNG as `*.candidate.text-mask.json`; a missing candidate mask is a hard
failure. Baseline promotion copies the approved mask beside the PNG as
`*.verified.text-mask.json`. Candidate and promoted comparison both use the union of the
approved/reference mask and current mask, so text growing and shrinking are treated
symmetrically.

Every PNG difference inside those rectangles is excluded from visual comparison,
including wording and glyph rasterization. Text correctness remains covered by UIA
names/values, localization tests, API contracts and journey assertions. Pixels outside
the mask remain governed by the rules below. Because a WPF `TextBlock` UIA rectangle can
be wider than its glyphs, every run writes a magenta `*.text-mask-overlay.png` for human
inspection; during comparison this overlay shows the actual reference/current union
passed to the comparator. A mask fails closed when a rectangle is invalid or outside the PNG, one
rectangle covers more than 15% of the frame, all rectangles cover more than 55%, or the
current side grows the approved/reference union by more than 5% of the frame. These
bounds keep nearby control structure from being silently exempted. `Edit` and `Document`
rectangles are deliberately not masked because their UIA bounds commonly include the
whole input surface rather than only its glyphs.

When two captures are not byte-identical, both the candidate stability gate and the
promoted-baseline gate apply the same bounded predicate
(`MesIngest.Watch.UiTests/WatchWindowVisualEquivalence.cs`). The ordinary
`bounded-neutral` path counts two captures as visually equivalent only when **every**
rule holds:

| # | Rule | Rejects |
| --- | --- | --- |
| 1 | identical frame dimensions | resize, DPI and layout-container changes |
| 2 | ink mask unchanged | backstop for rule 4; see the note below |
| 3 | achromatic delta (ΔR = ΔG = ΔB) | accent, status and theme colour changes |
| 4 | per-channel &#124;Δ&#124; ≤ 3 | contrast, opacity and brightness changes |
| 5 | ≤ 24 differing regions, each ≤ 128x48 px | global gamma shifts, large-area repaints |
| 6 | ≤ max(512, 0.05% of the frame) differing pixels | slow erosion of a baseline |
| 7 | alpha channel unchanged | compositing changes |

Rule 4 is what actually rejects moved text, a different glyph, a different font or
weight, a moved control and an added or removed element: all of those turn background
into ink somewhere, which is a swing of tens of levels, not 3. Rules 5 and 6 then bound
how much of the frame may differ at all.

Rule 2 states that requirement directly instead of leaving it implicit in a magnitude
bound. It is deliberately unreachable while `MaxAbsoluteDelta` stays at 3 — a pixel
cannot cross from clearly-ink to clearly-background within 3 levels, and the band around
the threshold keeps it from flaking on the threshold itself. It exists so the invariant
survives someone raising the magnitude bound: whoever does that must confront the ink
rule rather than silently losing the guarantee. `Moved_ink_is_rejected` records which
rule fires today.

Per run, at most 4 steps may contain unmasked differences accepted by
`bounded-neutral`, and at most 1536 unmasked differing pixels may be accepted in total.
A capture classified `text-masked+bounded-neutral` consumes that ordinary budget for
its unmasked pixels. Exceeding either budget fails the run.

### Edge-raster-only path

Unmasked rasterized edges can contain hundreds of independent small components and exceed
the ordinary pixel/component budgets even though every changed sample moved by only
one or two levels. The `edge-raster-only` path accepts that cross-process rasterization
class only when all of these rules hold:

1. frame dimensions and alpha are unchanged;
2. every RGB channel changes by at most 3;
3. the ink-threshold band is not crossed;
4. every changed pixel lies within 4 pixels of a contrast of at least 12 in both frames;
5. no connected component exceeds 128x48 pixels; and
6. changed pixels do not exceed max(2048, 1% of the frame).

There is no component-count limit for this path because glyphs naturally form many
small components. Its pixels and steps are reported but do not consume the ordinary
4-step/1536-pixel run budget. This is an edge-raster classification, not semantic OCR:
PNG comparison cannot distinguish renderer-produced `#707070 -> #717171` from an
intentional one-level foreground-brush edit. That visually indistinguishable case is
therefore accepted when every rule above holds. Global or large flat-fill gamma shifts,
connected edges wider/taller than the component bounds, ordinary-contrast moved or
different glyphs that create a delta above 3, alpha changes, and any per-channel delta
above 3 still fail. Very low-contrast edge changes that remain within every bound are
intentionally treated as visually indistinguishable raster variance.

Acceptance is never silent. Every accepted capture writes
`visual-equivalence-accepted.json` plus `<step>.equivalent-{expected,actual,diff}.png`
into the evidence directory, and the run logs
`WATCH_WINDOW_VISUAL_EQUIVALENCE_ACCEPTED: step=… pixels=… maxDelta=… classification=…`.
Any step that used text masking or either tolerance path must be listed in the ticket
evidence and looked at by the repository owner, exactly like a `received` file.

The predicate is covered by `WatchWindowVisualEquivalenceTests` and, against real
golden-machine captures, by `WatchWindowVisualEquivalenceGoldenFixtureTests`
(pointed at `.artifacts/golden-renderer` through
`MESINGEST_WATCH_GOLDEN_FIXTURES`; it skips by name when the captures are absent).
Keep the real-capture tests: an early revision of the predicate silently reported
*zero* differences for every real pair because it normalised pixel formats through
`Graphics.DrawImageUnscaled`, which rescales by the ratio of the source and
destination resolutions — the captures carry 95.99 DPI while a new `Bitmap` defaults
to 96. Synthetic fixtures alone did not catch it.

Do not tune either path's bounds to make a failing run pass. If a real change is
visually equivalent but exceeds them, that is a baseline update with the usual
approval, not a tolerance change.

## Repetition count

`-Runs` defaults to **3** for every stability gate. A full journey run costs roughly
two and a half minutes on `win11-01` — measured 2026-09-02, three candidate runs in
9m12s including checkout, restore and build — so 3 runs is under ten minutes and 20
runs is close to an hour; the default is set so that routine verification stays
usable.

The count used to be 10 because repetition was the only defence against
nondeterministic rasterisation: the gate could not tell a harmless antialiasing flip
from a regression, so it had to see enough runs to notice the flip existed. The
bounded predicate above now *classifies* that difference instead of merely detecting
it, which changes what repetition is for. A real change fails on the first run that
shows it, whatever the count.

Repetition still has one job the predicate cannot do: catching genuinely intermittent
behaviour — a race that occasionally changes layout, data ordering that occasionally
reaches the capture, a control that occasionally keeps focus. Raise `-Runs` when a
ticket has reason to suspect that:

- a ticket touches async refresh, focus handling, virtualization or animation;
- a previous run of the same matrix produced an unexplained difference;
- a baseline is being promoted for a page whose journey is new.

Measured reference for calibrating the choice: the Ticket 23 antialiasing flip
appears in roughly 12% of runs (12 of 102 historical runs; 3 of 20 in the validation
batch `ticket-23-visual-equivalence-gate/run-20260818-222727`). A defect at that rate
has about a 33% chance of being missed by 3 runs and 8% by 20. Choose the count from
the rate you need to detect, and record the choice and its reason in the ticket
evidence.

## DPI validation

Do not change the calibrated `win11-01` desktop away from 100% for Ticket-level
DPI work — it is a CI runner, and a changed scale silently mis-renders every
later baseline run. Export/import a disposable clone on the factory server's
Hyper-V with a new VM ID, disconnect its network adapter before boot, and change
DPI only inside the clone.

- 125% means 120 DPI.
- 150% means 144 DPI.
- The interactive environment report must prove the effective DPI; registry
  values read from a non-interactive session are not sufficient.
- Run `watch-ui-journeys` at each required scale.
- Retrieve evidence, remove residual processes, stop and delete the clone, then
  remove the exact export/import directories.
- Recheck `win11-01` is 1920x1080, 96 DPI, Explorer is in session 1, and the
  three runner listeners are back — `Get-Process Runner.Listener` should show the
  desktop one in session 1. It does not restart itself; see
  `remote-ops/factory-server/docs/USAGE.md` section 8.

## Evidence contract

Use a unique directory per attempt; never overwrite earlier evidence:

A workflow run gives this for free: the run id is the unique attempt, and the
`golden-renderer-<mode>` artifact holds the evidence tree. Download it with
`gh run download <id>`. Only copy evidence into `<repo>\.artifacts\` when a
ticket needs it to outlive GitHub's artifact retention.

```text
<workflow run id>/golden-renderer-<mode>/watch-window-visual/run-NN/<journey>/
<repo>\.artifacts\golden-renderer\ticket-<NN>\run-YYYYMMDD-HHmmss\
```

Record at least:

- source commit and dirty-diff identity — for a workflow run, the run's own SHA;
- environment JSON;
- workflow run id and job conclusion;
- restore/build/test logs;
- screenshots, UIA trees, Verify XML, and received/diff files when applicable;
- pass/fail/skip counts, with every skip named and assigned to a release gate;
- for a promotion, the stability result and which run was promoted.

Expected offline `NU1801`/`NU1603` warnings remain in logs. They are not failures
when restore/build/test exits successfully, and they must not be suppressed.

## Ticket checklist

Every affected ticket should contain:

```md
- [ ] Read `docs/agents/golden-renderer.md`.
- [ ] Ran the required golden-machine suites on the `golden-renderer` runner.
- [ ] Sent the real-window preview to the repository owner (visual changes only).
- [ ] Recorded the workflow run id and all named skips.
- [ ] `mode=verify` reports 0 received against the baselines on the branch.
```
