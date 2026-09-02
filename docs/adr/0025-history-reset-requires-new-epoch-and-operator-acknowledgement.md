# 历史重置产生新纪元并要求人工确认

无数据库备份意味着非计划丢库会同时丢失当前投影和永久归档键墓碑，自动重建后直接开放目录会静默改变旧键的安全身份。决定每次计划空库切换或不可恢复重建都创建新的 HistoryEpoch；非计划重建时 Watch 明确显示历史身份已重置，ExternallyReadableDemandCatalog 与执行承诺读取持续返回 503 `INGEST_NOT_CURRENT`，直到运维人员提交 HistoryResetAcknowledgement 后才允许在新纪元开放，且所有快照、游标和目录条件身份必须绑定纪元。

**Status**: accepted

**Consequences**:
- 人工确认不能恢复墓碑，只是明确接受旧归档键后来可能被解释为新 Series 的风险。
- RestartBarrier 完成、连续成功轮次或磁盘恢复均不能代替 HistoryResetAcknowledgement。
- Watch 诊断读取可在确认前通过既有状态栏和概览健康区展示新纪元建立进度、HistoryEpoch、最早可用历史和本地管理指引，但不提供确认写按钮，也不得把旧纪元缓存挂到新纪元。
