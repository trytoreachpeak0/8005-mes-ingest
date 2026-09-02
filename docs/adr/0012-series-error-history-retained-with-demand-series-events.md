# Series 错误历史随 DemandSeriesEvent 永久保留

错误检索需要跨已归档 Series 和 Demand 世代解释曾经发生、恢复及复发的错误。决定将 DemandSeriesErrorPeriod 作为 DemandSeriesEvent 可推导的永久历史，不沿用 IngestAlert 已恢复 incident 的 365 天清理；默认查询仍限制为最近 7 天，全部历史通过 Host 有界分页读取，以审计完整性换取持续增长的历史存储与索引成本。

**Status**: superseded by ADR-mes-0022
