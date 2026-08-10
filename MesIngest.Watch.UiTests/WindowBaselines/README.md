# Real Watch window baselines

This directory holds exactly five pixel-exact `1440x900` client-area PNGs produced by
the formal `MesIngest.Watch.exe` under the calibrated 100% DPI, light-theme, zh-CN,
SoftwareOnly environment. The five names match `WatchWindowJourneyTests`.

Missing or changed baselines fail and write candidates/evidence under the run's artifact
directory. Tests never accept, rename, or overwrite a verified PNG.

A baseline proposal must include:

- a reason and linked implementation/spec ticket;
- before, after, and diff PNGs (an initial baseline records `before=(none)`);
- the exact environment manifest;
- a non-submitter reviewer;
- product/business confirmation when interaction, wording, hierarchy, or status color changed.

First show the final real-window previews to the user and obtain explicit approval. Then
generate a fresh candidate matrix and require it to be byte-identical for 10 consecutive
calibrated runs. Use `New-WatchWindowBaselineProposal.ps1` to assemble the review package.
Only after the recorded non-submitter review may a maintainer copy `after.png` to the
matching `*.verified.png`. Run 10 consecutive comparisons against the promoted matrix and
require zero received files. Baseline approval does not establish the release gate; the
complete gate needs 50 additional consecutive passes.

The proposal command requires a `ChangeType`. `Interaction`, `Copy`, `Hierarchy`, and
`StateColor` proposals are rejected unless `-ProductOrBusinessConfirmed` is supplied.
