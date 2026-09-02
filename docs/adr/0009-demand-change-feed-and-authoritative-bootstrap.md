# Demand 变更账本采用有限保留与权威 Bootstrap

持有 TransportDemand 本地镜像的下游不能靠重复下载全表或 DATES 时间游标可靠获知 GONE。决定在 SQL Server 中持久化单调 BIGINT sequence 的 DemandChangeFeed，并与 Demand CREATED/GONE 在同一事务提交；它只发布这两类业务变化，不发布每轮 last-seen。账本默认保留 48 小时且可配置；过期游标返回 `410 SYNC_CURSOR_EXPIRED`，消费者以“全部 VISIBLE + GoneAt 最近 24 小时 GONE”的一致高水位快照权威替换本地当前镜像，再继续增量。GONE Demand 本体仍永久保留。

**Status**: accepted

**Considered Options**:
- 仅内存通知/WebSocket（拒绝：下游离线、网络断开或 Host 重启会永久漏掉 GONE）
- ChangeFeed 永久保留（未采纳：下游只关心所有当前 VISIBLE 与最近 24 小时 GONE；允许过期后重建可控制热账本）
- 以 DATES/UpdatedAt 作为同步游标（拒绝：DATES 是当前工序进入时间且不单调，更新时间会碰撞并被高频观察更新污染）
- 持久化有限账本 + 明确过期 + 权威 Bootstrap（采纳）

**Consequences**:
- 消费者必须持久化 sequence、幂等应用，并实现 410 后的 replace-style Bootstrap，不能只 merge。
- Bootstrap 期间需要 high watermark 协议，保证并发 CREATED/GONE 不丢失。
- ChangeFeed 是同步机制，不替代 TransportDemand 查询 API、IngestAlert 或 Oracle 原始快照。

