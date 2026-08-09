# MesIngest Watch visual-redesign throwaway prototype

> Three visual redesigns of the `TransportDemand` browse route plus two selected-design relationship states, switchable with `--variant=A|B|C|D|E` on the existing WPF prototype window.

This project exists only to answer “验证以 MES 任务与 IngestAlert 为中心的简化 WPF 原型”. It uses representative fake data and never contacts a Host. It deliberately excludes performance analysis, diagnostics, trace views, telemetry upload, diagnostic export, and alert handling actions.

Run from `mes/ingest/csharp`:

```powershell
dotnet run --project .\MesIngest.Watch.Prototype\MesIngest.Watch.Prototype.csproj -- --variant=A
```

Language boundary:

- Chinese is used for navigation, actions, explanations, and operational conclusions.
- English is used for entity names, schema fields, enum values, task types, and alert codes.
- A heading never combines both languages with a slash.

Variants:

- `A` — 运维工作台：深色导航、中央列表、右侧字段解释器。
- `B` — 关系解释器：按来源字段、本地实例、异常关联三层解释记录。
- `C` — 聚焦浏览器：筛选侧栏、全宽列表、底部解释详情。
- `D` — 任务关联告警：采用 A 的主导航和 C 的筛选侧栏，解释当前 `TransportDemand` 关联哪些 `IngestAlert` 以及匹配依据。
- `E` — 告警关联任务：与 D 共用结构，从当前 `IngestAlert` 反向展示精确目标、业务键范围或不可定位范围。

Selected synthesis after review: keep A navigation, add C filtering, and upgrade B's relationship explanation into a bidirectional inspector. Relationship scope is never flattened into a fake many-to-many link: exact `DemandId`, business key, task type, and global alerts remain visibly distinct. `REAPPEAR_AFTER_GONE` keeps separate previous GONE and new visible targets.

The bottom switcher or `Left`/`Right` keys changes variants. Direct page keys are `--page=OV|TS|AL|ST`. Review scenarios are `--scenario=Active|Healthy|Offline|Empty|Paging`.

The fake data includes all six production `TASK_TYPE` values, all seven MES fields (`TASK_TYPE/SUBLOT/AREA/EQP/STEP/DATES/PACKAGE`), local TransportDemand projection fields, and all six production IngestAlert codes. `HOST_UNREACHABLE` is shown only as connection state and never as an IngestAlert.

Optional deterministic 2K capture:

```powershell
dotnet run --project .\MesIngest.Watch.Prototype\MesIngest.Watch.Prototype.csproj -- --variant=A --page=TS --scenario=Active --width=2560 --height=1440 --capture=.\tasks-2k.png
```

Selected D/E production-visual references can be rebuilt at the approved review size:

```powershell
dotnet run --project .\MesIngest.Watch.Prototype\MesIngest.Watch.Prototype.csproj -- --variant=D --page=TS --scenario=Active --width=1440 --height=900 --capture=.\demand-to-alerts-1440x900.png
dotnet run --project .\MesIngest.Watch.Prototype\MesIngest.Watch.Prototype.csproj -- --variant=E --page=AL --scenario=Active --width=1440 --height=900 --capture=.\alert-to-demands-1440x900.png
```

Expected SHA-256 values are `5D4BB9F10C79BAD1289518A8ED237317875A418A9446682D41AFC1FD324C87A4`
and `E0F1BF277691665A231DA069D0B3B8A763AFCA62C76CF003023C3B23FC9DAC5C`, respectively.

Review these questions:

1. Which shell makes it fastest to answer “is ingest healthy, and what needs attention?”
2. Should the six task types or the active-alert queue remain visible across pages, or should both stay page-local?
3. Does the task page make the boundary between seven MES input fields and local TransportDemand projection fields unmistakable?
4. Are the explicit filters, VISIBLE/GONE separation, cursor paging, detail panes, and Alert → Demand jump sufficient for read-only operations?
5. Is the settings page minimal enough, with no observability storage or diagnostic-export configuration leaking back into scope?

After review, capture the winning decision in the wayfinder ticket and move this full prototype set to a throwaway branch. Do not promote this code directly to production.
