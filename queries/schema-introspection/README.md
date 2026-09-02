# MES_SCHEMA_INTROSPECTION

以单条只读查询核验六类运输任务涉及的 Oracle Schema、字段类型和声明长度。正式 SQL 见 [`query.sql`](query.sql)，机器可读契约见 [`query.toml`](query.toml)。

## 契约

- 参数：无。
- 输出：当前用户、Schema、会话/数据库时区，以及每个必需字段的对象名、字段名、`FOUND`、类型、声明长度、精度、可空性和默认值。
- 每个必需字段固定保留一行；未找到时 `FOUND='N'`，其余字典列为空。
- 本查询不扫描业务表统计实际最大长度，适合统一 runner 的单语句只读边界。

## 实验与证据关系

每次核验应按 `MES_SCHEMA_INTROSPECTION` 记录数据库用户、Schema、时区和执行时间。字段缺失结果及长度结论进入 `evidence/`，对外样本不得包含业务数据。实际最大长度扫描属于高负载研究，必须另建实验并取得客户 IT 批准；旧版多语句探测 SQL仅保留作迁移来源。
