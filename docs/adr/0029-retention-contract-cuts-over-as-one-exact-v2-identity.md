# 有界历史契约作为一个精确 V2 身份整体切换

HistoryEpoch、历史过期410、当前性503和有界保留状态改变了现有读取语义，但本项目已经要求 Host、Watch 与 reference consumer 精确匹配且不混跑。决定同时提升 NewMesIngestContract 的 contractVersion 与 schemaVersion，继续只发布唯一 `/api/v2` 路由并将三者整包切换；不静默沿用旧身份、不长期维护 V2/V3 双路由，以停机整体升级换取单一可解释契约。

**Status**: accepted
