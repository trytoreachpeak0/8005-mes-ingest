# Host 拥有有界历史清理

15 天保留规则涉及当前状态、活动错误、Series 归档资格、墓碑和公开历史边界，交给独立 SQL Agent 或计划脚本会让删除语义脱离应用契约。决定由 MesIngest Host 的单实例后台流程每小时计算 Host UTC 截止边界并以有行数和时间预算的小事务推进清理，轮询优先且失败可续；清理失败形成 Current Attention，只有存储达到 ADR-mes-0020 的阈值时才进入 StoragePressurePause。

**Status**: accepted

**Consequences**:
- 清理必须幂等、可中断，并与同一 Host 的投影事务和 schema 版本共同发布测试。
- 单个 Series 的墓碑写入与详细历史删除服从 ADR-mes-0022 的同事务原子边界，批处理进度不得拆开该边界。
- SQLSERVERAGENT 和外部 Windows 计划任务不拥有业务数据清理，也不得绕过 RetentionEligibleDemandSeries 与墓碑规则。
- 单批大小、时间预算和检查间隔是可配置运维参数，不改变 15 天领域保留语义。
