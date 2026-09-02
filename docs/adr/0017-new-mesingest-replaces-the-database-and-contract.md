# 新 MesIngest 以空库和新契约整体替换旧版

新领域模型与旧版冻结字段、incident 告警、ChangeFeed 和读取 DTO 的含义已根本不同，迁移旧记录会制造无法证明的 DemandSeries、资格与错误历史。决定在停机切换时删除旧 MesIngest 数据库并由新版本建立空库，不迁移旧业务历史、不兼容旧 schema/API/DTO，也不支持新旧 Host、Watch 或外部消费者混合运行；新历史从首个完整成功 MesTaskUnionRound 开始，回退只能恢复整套旧部署及其独立备份。

**Status**: accepted

**Consequences**:
- 删除数据库是部署操作，不由 Watch 或日常运行时自动执行；实施前仍须确认目标实例并按现场切换流程处理备份与停机。
- 新 Host、Watch 及外部消费者必须作为一个契约版本共同切换。
- 旧 `.scratch` 规格中的字段冻结、IngestAlert incident、DemandChangeFeed 和旧分页契约不得作为兼容要求带入新规格。
