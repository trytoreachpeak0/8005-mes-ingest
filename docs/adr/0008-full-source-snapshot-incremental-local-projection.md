# MES 完整源快照与本地增量投影/有界读取分离

MesIngest 判断 Demand 消失需要同一轮客户批准 SQL 的完整活动集合，因此继续以只读 Oracle 全快照作为对账输入，不把“只查新增”冒充消失语义，也不修改客户 SQL、索引、视图或执行 DDL。SQL Server 持久化改为只写本轮差异：正常热路径只加载 VISIBLE 与必要 pause 状态，GONE 永久保留但不再每轮加载或重写；读取 API 使用服务端筛选、稳定 `DATES + DemandId` 排序、索引和有界游标分页。这样保留完整缺失判断与审计身份，同时让轮询/API 成本不随全部 GONE 历史线性重写。

**Status**: accepted

**Considered Options**:
- Oracle 只查新增/最近时间（拒绝：没有可靠删除或变化契约时无法判断 GONE）
- 每轮删除并重建全部 SQL Server 投影（拒绝：永久 GONE 增长会放大写入、锁等待和远程数据库往返）
- Watch/API 每次下载全部历史后本地筛选（拒绝：数据库、JSON、网络和 UI 成本均无界）
- 完整源快照 + 本地差异写入 + 分页读模型（采纳）

**Consequences**:
- Oracle 查询慢只能被准确观测并交客户 IT；本项目不通过未经授权的 MES SQL/DDL 优化规避。
- GONE 可永久按 DemandId 追溯，但默认浏览窗口按 GoneAt 有界，所有 VISIBLE 永远可见。
- SQL Server schema、store 接口和 Watch HTTP 契约需要一次兼容升级；当前无旧外部消费者需保留裸数组。

ADR-mes-0022 仅取代本 ADR 中 GONE 与详细历史永久保留的部分；完整 Oracle 源快照、本地差异投影和有界读取的架构决定继续有效。

