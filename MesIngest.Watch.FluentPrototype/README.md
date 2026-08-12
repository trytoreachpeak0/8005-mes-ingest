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

The overview redesign asks: **how should the landing page summarize every
operational page without turning Host status into the main content?** Three
layouts are available via `--page=overview --variant=A|B|C`: A uses page cards
plus cross-page highlights, B puts attention items first, and C presents a
compact page-status table. **Variant A was selected on 2026-08-12**; B/C remain
rejected prototype comparisons.

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
```

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
```

Use the real navigation items to move between pages. Fake data exists only to
make lifecycle and readability edge cases visible during review.
