# MesIngest Watch selected UI prototype

> PROTOTYPE — throw away after review. Do not promote this XAML directly to production.

This prototype answers: **how should the selected compact Watch shell expose all
DemandSeries with per-series lifecycle/events, while retaining a separate
readability audit for what external consumers can and cannot currently see?**

The current extension also asks: **which information hierarchy makes local
AreaFilterProfile files easiest to manage without confusing Watch display scope
with external readability or Dispatch scope?** Variant A — the master-detail
editor — was selected. Variants B/C remain switchable prototype comparisons via
`--variant=A|B|C` and the floating review bar.

The new error-search extension asks: **how should an operator select an error
category and find every DemandSeries that has ever matched it, including errors
that later recovered?** It has three deliberately different layouts, switched
with `--variant=A|B|C`: A is category navigation plus evidence detail, B is a
search-first faceted result workspace, and C is a category overview that drills
down into a result table. No variant has been selected yet.

The live-sync AREA rework asks: **once the page drops the scope banner, the
reload button, the "AREA（每行一个）" strip and the save/discard row in favour of
debounced autosave plus a directory watcher, how should the remaining surface be
arranged?** Three deliberately different layouts are available via
`--page=area-live --variant=D|E|F`: D keeps the master-detail split and moves
every file command into the left profile card, E drops the master column for a
top chip strip so the editor owns the full width, and F collapses everything
into one full-width list whose selected row expands its editor inline. Add
`--scenario=invalid` to see how each one reports an invalid draft. **Variant D
was selected on 2026-08-20**; E/F remain rejected prototype comparisons. E was
rejected because the top chip strip carries no marker for an invalid profile, and
F because its inline editor is shorter than the layout it replaces.

The overview redesign asks: **how should the landing page summarize every
operational page without turning Host status into the main content?** Three
layouts are available via `--page=overview --variant=A|B|C`: A uses page cards
plus cross-page highlights, B puts attention items first, and C presents a
compact page-status table. **Variant A was selected on 2026-08-12**; B/C remain
rejected prototype comparisons.

The notification feedback extension asks: **how should the approved overlay
feedback contract feel inside the selected Watch shell?** Run
`--page=feedback --variant=A|B|C`. A uses independent Fluent toast cards, B
groups active notices into one activity surface, and C uses compact
command-first tiles. The page directly demonstrates silent automatic refresh,
user-operation success, first-occurrence fault contraction, repeated-fault
coalescing, three-item priority/stacking, recovery, AREA concurrent-write
`ContentDialog`, narrow-window reflow, timer pause, and reduced motion. This is
throwaway presentation code; no variant is production-ready. **Variant A —
independent Fluent toast cards — was selected on 2026-08-26**; B/C remain
rejected prototype comparisons.

The bilingual semantics extension asks: **without changing the selected
eligibility-audit master-detail hierarchy, how should localized meaning, raw
protocol codes, field identities, absolute time, units, and six distinct
missing/query states be arranged?** Run `--page=bilingual --variant=A|B|C`.
A is a localized semantic workbench, B is a header-led field semantics ledger,
and C is a grouped master list plus evidence narrative. The floating review bar
switches variants and preview language in memory; arrow keys also cycle variants.
This is a review-only implementation of ADR-0031, not the production resource
architecture. **Variant A — semantic workbench — was selected on 2026-08-27**;
B/C remain rejected prototype comparisons.

The selected information architecture is:

- `概览`
- `需求系列` — every Tracking/GONE/Archived series, Demand generations, lifecycle, and ordered `DemandSeriesEvent` facts
- `资格审计` — every TransportDemand split into currently externally visible and invisible demands, with exact reasons such as data blockers, GONE, or archived Series
- `错误检索` — historical error-category search across DemandSeries, preserving matched Demand generations and active/recovered state
- `AREA 筛选` — local TXT-backed display profiles shared by DemandSeries and 资格审计
- `接入告警`
- compact Host state and Settings in the navigation footer

The Watch remains read-only. It deliberately does not show Dispatch AREA scope,
AREA-to-station mapping, route cost, nearest task, claim state, suppression, or
orders.

Run from `mes/ingest/csharp`:

```powershell
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=overview --variant=A
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=overview --variant=B
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=overview --variant=C
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=series
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=A --scenario=stack
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=A
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=A --scenario=english
```

Review pages:

```powershell
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=series
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=audit
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=errors --variant=A
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=errors --variant=B
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=errors --variant=C
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area --variant=A
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area --variant=B
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area --variant=C
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=D
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=E
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=F --scenario=invalid
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=A --scenario=stack
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=B --scenario=fault
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=C --scenario=conflict
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=A --scenario=narrow-reduced
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=A
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=B
dotnet run --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=C
```

The review bar on `--page=area-live` also carries a 切到非法态 button, so the
invalid-draft state can be toggled without restarting.

Deterministic local captures (not golden-machine evidence):

```powershell
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=series --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\selected-series.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=audit --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\selected-audit.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=errors --variant=A --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\error-search-a.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=errors --variant=B --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\error-search-b.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=errors --variant=C --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\error-search-c.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area --variant=A --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-filter-a.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area --variant=B --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-filter-b.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area --variant=C --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-filter-c.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=D --scenario=healthy --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-live-d-valid.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=D --scenario=invalid --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-live-d-invalid.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=E --scenario=healthy --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-live-e-valid.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=E --scenario=invalid --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-live-e-invalid.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=F --scenario=healthy --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-live-f-valid.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=area-live --variant=F --scenario=invalid --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\area-live-f-invalid.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=A --scenario=stack --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\feedback-a-stack.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=B --scenario=fault --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\feedback-b-fault.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=C --scenario=stack --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\feedback-c-stack.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=C --scenario=conflict --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\feedback-conflict.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=feedback --variant=A --scenario=narrow-reduced --width=760 --height=820 --capture=.\MesIngest.Watch.FluentPrototype\review\feedback-narrow-reduced.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=A --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\bilingual-a-zh.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=B --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\bilingual-b-zh.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=C --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\bilingual-c-zh.png
dotnet run -c Release --project .\MesIngest.Watch.FluentPrototype\MesIngest.Watch.FluentPrototype.csproj -- --page=bilingual --variant=A --scenario=english --width=1440 --height=900 --capture=.\MesIngest.Watch.FluentPrototype\review\bilingual-a-en.png
```

Use the real navigation items to move between pages. Fake data exists only to
make lifecycle and readability edge cases visible during review.
