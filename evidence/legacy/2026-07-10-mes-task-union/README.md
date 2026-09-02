# 2026-07-10 MES 五类任务合并查询旧证据

## 状态

本目录是重构前历史归档，只作历史证据，禁止标记为 `latest`，禁止直接晋升为当前 samples。

## 已知限制

- `MES五类任务合并查询result.csv` 只有 6 列：`TASK_TYPE、SUBLOT、AREA、EQP、STEP、DATES`。
- 该结果缺少当前 7 字段契约要求的 `PACKAGE`。
- 测试由 Navicat 手工执行，旧记录只包含开始/完成时间、2.75 秒耗时、630 行和“无 Oracle 错误”。
- 旧产物没有符合现行规范的 run_id、查询哈希、文件哈希、客户批准引用和工厂回传 manifest。
- 因此它不能证明当前 `MES_TASK_UNION` 查询通过契约、质量、等价性或性能验收。

## 使用约束

- 可用于说明 2026-07-10 曾有一次 6 字段查询执行及其当时结果。
- 不得补写不存在的元数据或把后续结论回填成当时证据。
- 新验证必须按 [`../../../experiments/definitions/mes-task-union-validation/plan.md`](../../../experiments/definitions/mes-task-union-validation/plan.md) 创建新的不可覆盖 run。

归档文件：

- `MES五类任务合并查询result.csv`
  - SHA-256：`3B26A5F1C121DB5D32643B55A9D53CE8D9F8DE27764C411499E69B161097D1C3`
- `MES五类任务合并查询测试记录.md`
  - SHA-256：`A64F316ADC8E5B9C5CDA0EB70D76603B74FEE320F0D382FE88E6D79498DA63E3`
