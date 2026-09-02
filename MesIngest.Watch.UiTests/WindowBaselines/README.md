# Real Watch window baselines

This directory holds 11 approved `1440x900` client-area PNGs and their UIA Text-region
mask sidecars, produced by
the formal production `MesIngest.Watch.exe` under the calibrated 100% DPI,
light-theme, zh-CN, SoftwareOnly environment. The names match
`WatchProductionBaselineMatrix` and cover Overview (normal, expanded navigation,
and retained failure), Settings (normal and validation), DemandSeries,
ReadabilityAudit, AREA profiles, Error Search, CurrentIngestAttention, and the
attention-to-error drill.

Missing or changed baselines fail and write candidates/evidence under the run's artifact
directory. Tests never accept, rename, or overwrite a verified PNG.

UIA `Text` element pixels are excluded from visual comparison through the per-capture
`*.text-mask.json` evidence. Candidate pairs and promoted runs require both masks and
compare their bounded union. Each capture also emits a magenta mask overlay for review.
Text content remains asserted by UIA/localization/journey tests; invalid, oversized or
unexpectedly expanded masks fail closed.

## Replacing them

Dispatch `.github/workflows/golden-renderer.yml` with `mode=candidates`. It generates a
fresh candidate matrix and requires it to be stable for `-Runs` consecutive calibrated
runs (default 3; see "Repetition count" in `docs/agents/golden-renderer.md` for when to
raise it). A run is stable when its captures are byte-identical to run 1, or when the
bounded visual-equivalence predicate accepts them; every accepted capture is recorded in
the evidence directory and belongs in the promotion commit's message.

Send the candidates to the repository owner, then promote **run-01** — the run the
stability manifest was built from. Copy each `*.candidate.png` to the matching
`*.verified.png` **and** each `*.candidate.text-mask.json` to `*.verified.text-mask.json`.
Both files, always together: a PNG promoted without its mask fails the next comparison on
mask growth rather than on pixels, which reads like a rendering problem and is not one.
Then dispatch `mode=verify` and require zero received files.

There is no proposal record and no second reviewer. A script that manufactured them
existed until 2026-09-02 and was deleted, because this repository has one owner and the
reviewer field could only ever be filled in with someone who does not exist. The
promotion commit is the record; write enough in it that the next reader can tell an
intentional change from a drifted machine.

Baseline approval does not establish the release gate; the complete gate needs 50
additional consecutive passes.
