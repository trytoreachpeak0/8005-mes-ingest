# Selected UI XAML baselines

This directory contains the active Ticket 11 Wpf.Ui matrix:

- 15 scenarios at `1440x900`;
- 4 scenarios at `2560x1440`;
- one reviewed `*.verified.png` and `*.verified.xml` pair per scenario.

The matrix was established on `GPT-WIN11` at 1920x1080, 96 DPI, Windows light
theme, `zh-CN`, `China Standard Time`, Microsoft YaHei UI/Consolas, and WPF
`SoftwareOnly`. The final four-page real-window preview was accepted by the user.
All 19 received pairs were byte-identical for ten consecutive candidate runs; each
scenario then received an individual before/after/diff review. After approval, the
entire verified matrix matched for ten further consecutive runs with zero received
files (`WATCH_XAML_STABLE`).

The rejected pre-reset approved files remain one directory above as historical
evidence and are not loaded by Verify.Xaml. Do not copy or overwrite those files when
updating this matrix. A future change must repeat the calibrated environment probe,
10-run candidate gate, per-scenario review, and 10-run approved gate.
