# MesIngest 第一期实现技术选型：Service + WPF + SQL Server

在 [ADR-mes-0006](0006-mes-ingest-vs-dispatch.md) 的产品边界下，第一期用全 C# 交付：Windows Service 常驻（轮询对账、SQL Server 投影、Kestrel 只读 API），WPF 仅作可开关盯盘客户端；关 WPF 不停 Service。投影库用 SQL Server。Oracle 默认 Thin，保留 Thick+Instant Client 可切换，并以工厂连通探针验收（现网为 11g，Thin 可能失败则改配置，不改业务代码）。正式 SQL 以仓库 `mes/queries/` 为开发期唯一原稿，构建时拷入安装目录旁的 `queries/`，禁止在工程内另维护会漂移的 SQL 副本。凭证可配置、不进仓库。Web UI 与连接池/超时等细项留待后续，不阻塞本决策。

**Status**: accepted

**Considered Options**:
- Python + FastAPI（拒绝：团队要求全 C#）
- 仅 WPF 宿主（拒绝：关窗口会停轮询/API）
- Service + Web 第一期（可接受但延后；先 WPF）
- 投影用 SQLite（拒绝：直接 SQL Server）
- Oracle 只做 Thick（拒绝：偏好 Thin 部署；改为默认 Thin + Thick 兜底）
- SQL 嵌入程序集为唯一载体（弱拒绝：改 SQL 必重编；改为正式 query 原稿 + 发布拷贝）

**Consequences**:
- 部署单元为自包含安装目录（Service、配置、`queries/`）；WPF 为可选客户端。
- `meslab`（Python）继续只作工厂实验/复验工具，与生产 MesIngest 分离。
- Oracle 驱动与轮询实现细节在编码阶段再钉数值；模式切换必须可配置。
