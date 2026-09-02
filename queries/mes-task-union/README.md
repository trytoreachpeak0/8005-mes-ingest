# MES_TASK_UNION

一次执行六类 MES 运输任务查询，并在同一 Oracle 语句级一致性快照中返回统一列。正式 SQL 见 [`query.sql`](query.sql)，机器可读契约见 [`query.toml`](query.toml)。

## 契约

- 参数：无。
- 输出顺序：`TASK_TYPE`、`SUBLOT`、`AREA`、`EQP`、`STEP`、`DATES`、`PACKAGE`。
- `TASK_TYPE` 取值：`DIE_TO_WIRE_STAGING`、`DIE_TO_OVEN`、`WIRE_TO_GATE`、`WIRE_TO_OPTICAL`、`STAGING_TO_WIRE`、`WIRE_TO_NITROGEN`。
- 使用 `UNION ALL`，不得用 `UNION` 静默去重；重复候选也是语句级一致快照中的原始证据，必须保留进 `SUCCESS` 轮次，再由投影层按确定性规则处理冲突。
- 查询只读，不在 SQL 中增加上线时间过滤；上线时间及异常骤降保护由应用层处理。
- 发布、部署和运行时只承认 `service/queries/mes-task-union/query.sql` 这一份原始字节稿；批准版本为 `MES_TASK_UNION/sha256:54a140ad2ca6e67413b24d0566991adcd665f6514a742b417b4ed818fbe439ae`。发布脚本会生成邻接 `query.manifest.json`，任何缺失、空文件、篡改或第二份 `.sql` 都必须拒绝。

## 实验与证据关系

实验应通过 `MES_TASK_UNION` 定位本包，不复制 SQL。每轮实验记录查询版本、执行环境、时间和结论到 `experiments/` 与 `evidence/`；可公开结果须脱敏后放入 `samples/`。客户原始查询仅作为分支对比基线：

- 前五类：[`../../sources/customer/2026-07-16/mes-task-original-queries/`](../../sources/customer/2026-07-16/mes-task-original-queries/)
- 第六类：[`../../sources/customer/2026-07-24/mes-task-original-queries/`](../../sources/customer/2026-07-24/mes-task-original-queries/)

不得作为运行时依赖。
