# 持久化空间不足前暂停 MES 轮询

当数据库所在存储接近耗尽时，继续读取 MES 却无法原子持久化会制造没有可靠证据的处理结果。决定剩余空间低于 15% 时发出严重告警，低于 10% 时 Host 在发起下一轮 MES 查询前进入 StoragePressurePause，继续提供最后成功投影但不取得新快照、不推进缺席判定，并仅在运维人员确认空间与数据库健康已经恢复后退出；选择可解释的接入空窗，而不是冒险填满存储或查询后丢弃证据。

**Status**: accepted

**Consequences**:
- 暂停期间短暂出现又消失的 MES Demand 可能永远未被观察到，Watch 必须明确显示该证据空窗。
- StoragePressurePause 不是 Oracle 失败、SQL 写入失败或 TaskTypeProtection，不能复用它们的状态语义。
- Watch 的诊断、历史与最后成功投影仍可读取；ExternallyReadableDemandCatalog 及执行承诺点的最终 Demand 读取必须返回 503 `INGEST_NOT_CURRENT`，不得让外部消费者把陈旧事实当成当前目录，也不得用空集合伪造当前没有 Demand。
- Watch 在既有全局状态栏和概览健康区呈现暂停原因、最早可用历史与恢复指引，但不提供恢复写按钮；恢复属于 MesIngestLocalAdministration。
- 自动清理可以释放空间，但不得仅因阈值回升便自动恢复轮询；恢复仍需人工确认。
