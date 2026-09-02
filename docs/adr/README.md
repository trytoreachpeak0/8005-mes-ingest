# ADR Index

Architecture Decision Records for this repository. Cite as `ADR-mes-<NNNN>` (e.g. `ADR-mes-0001`).

这些 ADR 2026-09-02 从 `8005---AGV` 随 `mes/ingest/csharp/` 一起迁到本仓库。跨子系统的决策（`ADR-cross-*`）留在 `8005-agv-program/docs/adr/cross/`。

## mes — MES / 调度策略

| ID | Title |
|---|---|
| [ADR-mes-0001](0001-queueing-stall-handling.md) | QueueingStall：预防 + 下单前清积压；超时只报警 |
| [ADR-mes-0002](0002-order-hang-handling.md) | OrderHang：充电与普通分支分开 |
| [ADR-mes-0003](0003-charging-hang-auto-reassign.md) | 充电 OrderHang 由 MES 自动改派 |
| [ADR-mes-0004](0004-charge-hang-station-selection.md) | 充电改派选桩：允许集合内 NearStationQuery |
| [ADR-mes-0005](0005-charge-hang-retry-limit.md) | 充电自动改充重试上限 N=2 |
| [ADR-mes-0006](0006-mes-ingest-vs-dispatch.md) | MES 任务接入与调度拆开；第一期只交付接入投影 |
| [ADR-mes-0007](0007-mes-ingest-tech-stack.md) | MesIngest 第一期：Service + WPF + SQL Server |
| [ADR-mes-0008](0008-full-source-snapshot-incremental-local-projection.md) | MES 完整源快照与本地增量投影/有界读取分离 |
| [ADR-mes-0009](0009-demand-change-feed-and-authoritative-bootstrap.md) | Demand 变更账本采用有限保留与权威 Bootstrap |
| [ADR-mes-0010](0010-watch-business-read-only-with-telemetry-ingest.md) | MesIngestWatch 保持业务只读，并允许受限遥测追加 |
| [ADR-mes-0011](0011-series-error-catalog-owned-by-mesingest-contract.md) | Series 错误目录由 MesIngest 领域契约拥有 |
| [ADR-mes-0012](0012-series-error-history-retained-with-demand-series-events.md) | Series 错误历史随 DemandSeriesEvent 永久保留 |
| [ADR-mes-0013](0013-error-search-uses-as-of-event-history-snapshots.md) | 错误检索使用冻结时点的事件历史快照 |
| [ADR-mes-0014](0014-series-error-history-separated-from-current-ingest-attention.md) | Series 错误历史与当前接入关注项分离 |
| [ADR-mes-0015](0015-overview-uses-one-consistent-host-snapshot.md) | Watch 概览使用一个一致的 Host 业务快照 |
| [ADR-mes-0016](0016-readability-audit-has-its-own-projection-snapshot.md) | 资格审计使用独立的投影快照身份 |
| [ADR-mes-0017](0017-new-mesingest-replaces-the-database-and-contract.md) | 新 MesIngest 以空库和新契约整体替换旧版 |
| [ADR-mes-0018](0018-demand-series-detail-uses-modeless-inspector.md) | DemandSeries 详情使用单实例非模态 Inspector |
| [ADR-mes-0019](0019-routine-raw-observations-expire-critical-evidence-persists.md) | 普通原始观测有限保留，关键边界原始证据永久保留 |
| [ADR-mes-0020](0020-pause-mes-polling-before-storage-exhaustion.md) | 持久化空间不足前暂停 MES 轮询 |
| [ADR-mes-0021](0021-empty-cutover-deletes-old-database-after-inline-gate.md) | 空库切换通过同窗门禁后立即删除旧库 |
| [ADR-mes-0022](0022-mesingest-history-bounded-with-archived-key-tombstones.md) | MesIngest 详细历史有界，归档业务键永久留墓碑 |
| [ADR-mes-0023](0023-simple-recovery-without-database-backups.md) | MesIngest 使用 SIMPLE 恢复且不保留数据库备份 |
| [ADR-mes-0024](0024-host-owns-bounded-history-cleanup.md) | Host 拥有有界历史清理 |
| [ADR-mes-0025](0025-history-reset-requires-new-epoch-and-operator-acknowledgement.md) | 历史重置产生新纪元并要求人工确认 |
| [ADR-mes-0026](0026-current-and-historical-reads-use-separate-physical-paths.md) | 当前态与历史态使用分离的物理读取路径 |
| [ADR-mes-0027](0027-frozen-reads-are-nonblocking-and-commit-consistent.md) | 冻结读取非阻塞且提交一致 |
| [ADR-mes-0028](0028-high-risk-state-recovery-is-local-administration.md) | 高风险状态恢复只允许本地管理 |
| [ADR-mes-0029](0029-retention-contract-cuts-over-as-one-exact-v2-identity.md) | 有界历史契约作为一个精确 V2 身份整体切换 |
| [ADR-mes-0030](0030-one-time-cutover-mode-drops-only-the-proven-old-database.md) | 一次性切换模式只自动删除已证明的旧库 |
| [ADR-mes-0031](0031-watch-complete-bilingual-presentation-defaults-to-chinese.md) | Watch 提供完整中英文呈现并默认中文 |
| [ADR-mes-0032](0032-watch-simplified-chinese-uses-chinese-for-all-static-ui-text.md) | Watch 简体中文模式的固定界面文字全部使用中文 |
| [ADR-mes-0033](0033-watch-overview-events-carry-structured-operator-explanations.md) | Watch 概览事件在同一快照携带结构化操作员解释 |
