# MES 任务接入与调度拆开；第一期只交付接入投影

本地运输任务状态机若把 MES 快照对账与派车/装载/运送绑在同一模块，会阻塞「先做 MES 获取与盯盘」。决定将 **MesIngest（MES 任务接入）** 与调度系统拆开：第一期只实现接入——轮询 `MES_TASK_UNION`、幂等对账、重启恢复快照、`VISIBLE`/`GONE`、字段冻结与漂移告警、`PAUSED_ZERO_DROP`、只读 API 与盯盘页；产出 **TransportDemand**（稳定 `demand_id`）。`TASK_TYPE+SUBLOT` 是接入对账键和同时最多一条 VISIBLE 的约束；GONE 后同键再现时，MesIngest 保留旧 GONE 实例、分配新的本地 `demand_id` 并产生对账告警，不阻断投影，也不读取调度状态。调度自管派车状态并以 `demand_id` 挂接；本地取消后的永久抑制以 `TASK_TYPE+SUBLOT` 为键归调度所有，不写入接入侧。

**Status**: accepted

**Considered Options**:
- 继续做完整本地任务状态机再暴露接口（拒绝：与调度耦合，挡第一期）
- 接入只吐原始快照、不做跨轮对账（拒绝：丢失恢复快照与消失语义）
- 外部只用 `TASK_TYPE+SUBLOT` 挂全部实例状态（拒绝：缺少稳定的本地实例与审计标识；保留为对账和抑制键）
- 让 MesIngest 读取调度侧抑制并阻断投影（拒绝：形成反向依赖，混淆 MES 投影事实与是否派车的业务判断）
- 接入 + 调度拆开，第一期只做接入投影（采纳）

**Consequences**:
- `AGV系统业务与MES任务模型.md` 中厚状态机仍描述目标调度行为，但实现上须按「接入 vs 调度」分期阅读；接入范围以本 ADR 为准。
- 接入的 VISIBLE 列表只表示 MES 当前可见，不等于调度「当前可派」；调度须结合自身状态和永久抑制判断是否建立或恢复业务任务。
- MesIngest 对外发布完整当前 ExternallyReadableDemandCatalog，不接受 WorkType、车间 AREA 范围、车辆、地图或站点等调度参数；Dispatch 在可丢弃目录缓存上按自身 WorkType + AREA 白名单筛选，再负责 AREA 到站点映射、最近站点计算与任务选择。
- MesIngest 对 GONE 后同键再现分配新 `demand_id` 并告警是接入职责内的既定行为；新的本地投影实例不表示 MES 形成了新需求。
- 调度命中永久抑制的 TransportDemandKey 时不得建立或恢复业务任务、不得派车；该判断不改变 MesIngest 的投影和告警行为。
