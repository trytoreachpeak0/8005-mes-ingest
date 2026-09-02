# 冻结读取非阻塞且提交一致

Watch 冻结列表、分面和详情必须解释同一 ProjectionCommit，但 Serializable 会阻塞持续轮询，而普通 ReadCommitted 多语句读取可能混入并发更新。决定冻结读取必须来自一个提交一致且不阻塞投影写入的视图，优先采用短事务版本化读取或不可变快照读模型；仅按 ProjectionSequence 过滤但仍读取可变当前行的普通 ReadCommitted 不构成正确性证明，具体机制由真实 tempdb、执行计划和并发门禁选择。

**Status**: accepted

**Consequences**:
- ADR-mes-0015 的概览一致性和 ADR-mes-0016 的资格审计快照身份是强验收条件，不能为低锁降级为最终一致拼接。
- 实现不得默认退回长 Serializable 事务；若选择 Snapshot Isolation，必须把版本存储纳入2GB内存与16GB数据库环境的实测。
- 当前态与历史态虽走 ADR-mes-0026 的不同物理路径，同一响应仍必须绑定明确 HistoryEpoch 与 ProjectionCommit。
