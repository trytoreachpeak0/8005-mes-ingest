# 当前态与历史态使用分离的物理读取路径

把当前 Watch 刷新和任意冻结历史统一成一个大 SQL，会让每次热读取对全部 DemandRawObservation 排名并使成本随 15 天历史线性增长。决定当前态读取只依赖当前物化投影、当前条件和维护好的计数，禁止扫描或排名原始历史；历史详情与冻结 snapshot 使用独立、按对象或页有界的读取路径，两者共享领域投影含义但不共享物理查询计划，以更深的查询模块边界换取低内存和历史规模无关的热路径。

**Status**: accepted

**Consequences**:
- Overview 使用专用当前聚合或按 ProjectionCommit 缓存，不得通过完整历史 Browse 间接计算。
- 当前投影、筛选列和临时结构依据真实长度使用有界类型；DemandRawObservation 原文保持无损并在正式 schema 中对主要聚集及非聚集索引默认 PAGE 压缩，超界值形成异常而不截断原文。
- 若 15 天容量或热路径门禁仍失败，才重新打开 ObservationPayload/ObservationSet/Span 内容寻址方案；它不是第一版前置依赖。
