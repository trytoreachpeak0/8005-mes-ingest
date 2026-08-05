# MesIngestWatch V2 throwaway prototype

This project exists only to review the decision in “验证六区域 WPF 交互原型”. It uses representative fake data and never contacts a Host.

Run from `mes/ingest/csharp`:

```powershell
dotnet run --project .\MesIngest.Watch.Prototype\MesIngest.Watch.Prototype.csproj -- --variant=A
```

Variants:

- `A` — 全局态势看板：六区域左导航 + 平衡的健康/异常/Demand/链路概览。
- `B` — 事件处置优先：左侧长期保留只读事件队列，先选问题再核对证据。
- `C` — 证据时间线优先：右侧长期保留连接、Alert、poll trace 时间线。

The fixed bottom switcher or `Left`/`Right` keys changes variants. The scenario picker covers 健康、活动异常、慢链路、Host 离线、空状态和分页浏览. Arrow keys are not intercepted while an input or data grid cell has focus. A review can open directly on a scenario with `--scenario=Healthy|Alert|Slow|Offline|Empty|Paging`.

Review these questions:

1. Which variant lets an implementation/operations engineer decide “is ingest healthy?” fastest?
2. Is an active Alert queue or a chronology useful enough to occupy permanent screen space?
3. Does Demand/Alert drill-down preserve context without turning Watch into an action console?
4. Is per-page manual refresh, optional current-page auto-refresh, cancellation, and last-success preservation legible?
5. Which parts should be combined before page-level copy, density, and token decisions are locked?

Optional deterministic capture:

```powershell
dotnet run --project .\MesIngest.Watch.Prototype\MesIngest.Watch.Prototype.csproj -- --variant=B --scenario=Alert --capture=.\prototype-b.png
```

Delete or move this project to the prototype capture branch after the winning decision is folded into the product specification. It is not production code and is intentionally absent from `MesIngest.sln`.
