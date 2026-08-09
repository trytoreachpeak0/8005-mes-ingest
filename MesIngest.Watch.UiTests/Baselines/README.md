# MesIngestWatch legacy XAML visual baselines

The reviewed files directly in this directory belong to the rejected pre-reset UI. They
remain only as versioned history and are not loaded by Verify.Xaml after ticket 11's reset.
Do not overwrite, rename, or promote them as the selected UI.

The active matrix writes only to [`SelectedUi`](SelectedUi/README.md). `*.received.*`
candidates are ignored by Git and must never be committed.

Run from `mes/ingest/csharp` on the calibrated interactive Windows desktop:

```powershell
.\Invoke-WatchUiTests.ps1 -Suite watch-xaml-visual
.\Test-WatchXamlBaselineStability.ps1 -Runs 10
```

The runner requires a 1920x1080 active desktop, 96 DPI, Windows light app theme,
`zh-CN` culture/UI culture, Microsoft YaHei UI and Consolas, and WPF software rendering.
It exits before the test process starts when any value differs, so a mismatched machine
cannot create received files.

The stability script leaves the final `SelectedUi` received candidates in place only after ten
byte-identical runs. It never renames or accepts them. A human baseline update must include
the linked ticket/specification, environment report, before/after/diff review, and approval
from someone other than the author; UI copy, hierarchy, or state-color changes additionally
need product/business review.
