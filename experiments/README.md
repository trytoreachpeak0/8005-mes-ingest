# MES 实验定义

`experiments/definitions` 保存可复用、可评审的实验计划，不保存现场执行结果。执行结果按不可覆盖的 `run_id` 写入 [`../evidence/`](../evidence/)。

## 定义索引

- [`mes-task-union-validation`](definitions/mes-task-union-validation/plan.md)：验证六类任务合并查询的 7 字段契约、质量、等价性和性能。
- [`active-package-coverage`](definitions/active-package-coverage/plan.md)：评估活跃任务样本对容量对照表的当前覆盖率。
- [`package-universe-discovery`](definitions/package-universe-discovery/plan.md)：在客户 IT 批准范围内发现 PACKAGE 候选集合。
- [`toll-prefix-boundary`](definitions/toll-prefix-boundary/plan.md)：验证 `TOLL-` 前缀容量规则的匹配边界。
- [`sublot-basket-end-to-end`](definitions/sublot-basket-end-to-end/plan.md)：验证 SUBLOT、料盒数、冻结 PACKAGE、权威花篮数、完整仓位分配与失败阻断。
- [`legacy-program-sql-research`](definitions/legacy-program-sql-research/plan.md)：逐项研究旧程序 SQL 的业务意图、依赖和写操作风险，禁止直接执行来源快照。
- [`mes-ingest-factory-validation`](definitions/mes-ingest-factory-validation/plan.md)：MesIngest Service/探针/WPF 工厂验证与脱敏回传闭环（不走 meslab `import-run`）。

## 执行规则

1. 先复制对应 plan 的结论模板到本次 execution log，不修改定义来记录某次结果。
2. 必须记录关联问题、客户批准、安全边界、查询版本、参数、时间范围和运行环境。
3. runner 创建唯一 `run_id`；同一 run 目录不得覆盖、追加伪造或复用。
4. 原始结果只进入 evidence；只有 `import-run` 可以校验并生成 samples/latest。
5. 任何涉及生产 MES、较大扫描范围、历史全量或数据字典的实验，都必须取得客户 IT 对查询对象、时间范围、执行窗口和负载的明确批准。
6. 活跃任务样本只能描述“观察到的集合”，不能称为工厂全集。

证据格式和工厂回传流程见 [`../evidence/README.md`](../evidence/README.md)。
