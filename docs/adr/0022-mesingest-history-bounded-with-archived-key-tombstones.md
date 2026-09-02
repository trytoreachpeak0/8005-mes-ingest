# MesIngest 详细历史有界，归档业务键永久留墓碑

无限保留原始轮询、Demand 世代、事件和错误证据会让本地投影持续耗尽存储，但删除当前或归档身份会破坏外部可读安全。决定 DemandRawObservation 自 PollTrace 完成起提供 15×24 小时 RawObservationAvailabilityWindow；当前物化状态与活跃 DemandSeries 的结构化世代、事件及错误链不按年龄拆散，DemandSeries 成为 RetentionEligibleDemandSeries 后再保留 15×24 小时并整体清理；详细历史清理后永久保留极小的 ArchivedDemandKeyTombstone，确保旧 TransportDemandKey 重现时仍是 `LONG_GONE_BUT_VISIBLE` 且永不重新进入 ExternallyReadableDemandCatalog。

**Status**: accepted

**Consequences**:
- ADR-mes-0012 的永久错误历史、ADR-mes-0019 的永久关键原文以及 ADR-mes-0008 的永久 GONE 明细由本决定取代；不可变事实在保留期内不可改写，但不再承诺永久在线。
- 普通成功或失败 PollTrace 以 Host `CompletedAt`、关闭错误以 `EndedAt`、可清理 Series 以首次满足 RetentionEligibleDemandSeries 的 Host UTC 时点计算 15×24 小时；不得使用 MES `DATES` 或自然月。
- 已过期的 PollTrace、冻结快照或历史对象返回 410 `MES_INGEST_HISTORY_EXPIRED` 并公布最早可用 Host UTC 边界，不得用 404、空集合或残缺对象伪装。
- 永久墓碑只保存判断旧键身份与归档结论所需的最小事实，不保存可浏览详情；它是 15 天详细历史政策唯一允许的永久 MesIngest 记录。
- 清理一个 RetentionEligibleDemandSeries 时，必须在同一数据库事务中先幂等写入 ArchivedDemandKeyTombstone，再删除完整详细历史图；任一步失败均回滚，不允许出现已忘记归档身份的安全空窗。
