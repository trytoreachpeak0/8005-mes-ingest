# SUBLOT_BOX_COUNT

按单个 `SUBLOT` 查询其在各工序历史上出现过的最大料盒数。正式 SQL 见 [`query.sql`](query.sql)，机器可读契约见 [`query.toml`](query.toml)。

## 契约

- 必填绑定参数 `:sublot`：现场扫描的子批次号，对应 `fw_wip_box_his.lot`。
- 输出 `MAX_BOX_COUNT`：各工序料盒计数的最大值。
- 本查询不参与六类运输任务轮询；V1 在操作员输入 SUBLOT 后，用查询结果和冻结的 `PACKAGE` 容量计算权威 `ExpectedBasketCount`。
- 无结果、空值、非正数、失败、超时或容量无法唯一匹配时，不取消已有运输任务，但本次装载必须报错并停止：不分配仓位、不发送开锁指令，也不回退到现场追加数量。
- 只允许绑定变量和只读执行。

## 实验与证据关系

实验应覆盖多工序、空结果、空料盒和大数量场景，并按 `SUBLOT_BOX_COUNT` 记录证据。结果样本必须脱敏。来源说明见 [`../../sources/customer/2026-07-16/sublot-box-count/README.md`](../../sources/customer/2026-07-16/sublot-box-count/README.md)，来源快照不得作为运行时依赖。
