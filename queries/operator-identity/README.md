# OP_OPERATOR_IDENTITY

按工号解析 MES 操作员姓名，用于身份核验、操作会话和审计关联。正式 SQL 见 [`query.sql`](query.sql)，机器可读契约见 [`query.toml`](query.toml)。

## 契约

- 必填绑定参数 `:user_id`：工牌一维码或手动输入的工号，对应 `mv_fw_username.usercode`。
- 输出 `OPERATOR_NAME`：对应 `mv_fw_username.username`。
- 无结果表示身份未解析成功，应用不得建立操作会话。
- 本查询只负责“工号到姓名”，不声明岗位权限。
- 只允许绑定变量和只读执行；日志及证据中的人员信息必须脱敏。

## 实验与证据关系

实验应覆盖存在、不存在、重复结果、超时和连接异常，并按 `OP_OPERATOR_IDENTITY` 记录脱敏证据。来源说明见 [`../../sources/customer/2026-07-16/operator-identity/README.md`](../../sources/customer/2026-07-16/operator-identity/README.md)，来源快照不得作为运行时依赖。
