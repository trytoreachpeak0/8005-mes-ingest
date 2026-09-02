# 普通原始观测有限保留，关键边界原始证据永久保留

完整成功轮次的原始行当前随每次轮询重复增长，但永久错误历史只要求关键事实仍可被原文解释。本 ADR 曾决定普通 DemandRawObservation 提供 30 天 RawObservationAvailabilityWindow；该窗口与永久 DurableRawEvidence 方案已被 ADR-mes-0022 取代，当前有效契约统一为 15 天并以墓碑保留归档身份。

**Status**: superseded by ADR-mes-0022

**Consequences**:
- DemandSeriesEvent、DemandSeriesErrorPeriod 与其结构化证据继续永久保留，不因普通原始观测清理而缩短。
- 清理必须先证明一组原始行不属于 DurableRawEvidence；无法证明时不得删除。
- 所有原始行永久在线与关键证据有限提升两种语义不能混用，Host 与 Watch 必须公开实际可用边界。
