# 高风险状态恢复只允许本地管理

HistoryResetAcknowledgement 与 StoragePressurePause 恢复会重新开放外部当前读取或 MES 轮询，若放入 Watch 写 API 会破坏其业务只读边界并扩大远程攻击面。决定这些操作只通过数据库主机上授权管理员运行的 MesIngestLocalAdministration CLI/PowerShell 命令提交，命令执行身份、目标 HistoryEpoch、前后状态和时间必须审计；Watch 只显示状态和精确操作指引，不提供管理按钮，直接修改 SQL 表也不构成合法确认。

**Status**: accepted
