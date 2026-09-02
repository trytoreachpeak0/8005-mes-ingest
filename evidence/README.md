# MES 执行证据治理

`evidence/` 保存现场或受控环境中每次查询/实验的不可覆盖原始证据。这里的结果不是自动可复用样本，也不允许用 `latest` 覆盖历史。

## 目录规则

- 每次执行使用唯一 `run_id`，建议格式：`YYYYMMDDTHHMMSS+0800-<experiment>-<random>`。
- 工具固定路径：`evidence/runs/<run_id>/`；实验 ID、模式和 QUERY_ID 记录在 manifest。
- 一个 run 至少包含 `run-manifest.json`、`execution-log.md`、原始输出和 SHA-256 清单。
- run 创建后不可覆盖、改写或复用；补充说明应新建带时间和作者的附件，并在日志中说明。
- `legacy/` 只保存重构前历史产物，不得作为 latest。

## 哈希要求

1. runner 在工厂端完成输出后计算每个文件的 SHA-256。
2. manifest 记录查询文件哈希、参数摘要、输出文件哈希和 runner 版本。
3. 工厂回传包本身也计算 SHA-256，并通过独立渠道提供校验值。
4. 导入时先验证包哈希，再验证包内文件哈希；任一不一致即隔离，不得晋升 samples。
5. 导入后不得重新格式化原始 CSV；派生文件必须使用新文件名并记录来源。

## 工厂回传与导入流程

1. 现场按 [`../experiments/README.md`](../experiments/README.md) 的 plan 执行，runner 创建 run 目录。
2. 现场检查证据中不含 `config.ini`、密码、完整连接字符串或无关敏感字段。
3. 将整个 run 打包，记录包 SHA-256、回传人、回传时间和批准信息。
4. 仓库侧 `import-run` 解包到新的 evidence run 路径，验证 manifest、bundle、查询、原始输出和派生报告哈希；要求批准的查询在工厂执行前必须提供 `status=approved` 和批准引用。
5. 导入日志记录工厂来源、接收人、导入时间、工具版本和校验结论。
6. 只有通过校验且满足样本脱敏/最小化要求的 run，才可由 `import-run` 生成 [`../samples/`](../samples/) 的版本化样本和 latest 指针/副本。

禁止手工把 CSV 复制为 samples/latest。

## MesIngest 工厂验证回传

MesIngest Service 现场验证（`experiment_id=mes-ingest-factory-validation`）复用本目录的 **唯一 run_id、不可覆盖、先脱敏** 习惯，但产物形状是探针日志 + 只读 API JSON + manifest，**不是** meslab SQL query bundle。

1. 工厂按安装包内 [`../ingest/csharp/pack/FACTORY-VALIDATION.md`](../ingest/csharp/pack/FACTORY-VALIDATION.md) 与 `validation/RETURN-CHECKLIST.md` 执行并组包。
2. 回传前确认无 `appsettings.Local.json`、密码或完整连接字符串。
3. 仓库侧将整个 run 目录**手工复制**到 `evidence/runs/<run_id>/`；若路径已存在则拒绝覆盖，要求新 `run_id`。
4. 在该 run 的 `execution-log.md`「导入记录」填写接收人、时间与脱敏复查结论。
5. **禁止**对 MesIngest 验证包执行 `meslab import-run`；**禁止**晋升 [`../samples/`](../samples/)（samples 仅服务 SQL 查询样本通道）。
6. 维护者 / Agent 可用回传的 `demands` / `alerts` / `poll-health` JSON 与既有 `samples/mes-task-union` 或 File 源做人工对照（行数量级、字段形态、告警类型）；计划见 [`../experiments/definitions/mes-ingest-factory-validation/plan.md`](../experiments/definitions/mes-ingest-factory-validation/plan.md)。

模板随安装包：`mes/ingest/csharp/pack/validation/`（`run-manifest.example.json`、`execution-log.md`、`signoff.md`）。

「验证包已就绪」指仓库已交付清单与模板；「工厂已签字通过」仅当 `signoff.md` 由现场填写，且不阻塞本地后续开发。

## manifest 必填信息

- schema_version、run_id、experiment_id、query_id
- started_at、finished_at、timezone
- runner 名称/版本、查询版本/SHA-256
- 参数摘要和时间范围
- 环境逻辑名称，不记录真实 host 或用户名
- 客户批准人、批准引用、执行窗口和负载边界
- 输出文件名、字节数、行数、SHA-256
- source_factory、returned_by、returned_at
- 是否脱敏、是否含个人信息、校验状态

模板见 [`round-template/run-manifest.example.json`](round-template/run-manifest.example.json) 和 [`round-template/execution-log.md`](round-template/execution-log.md)。

## 旧证据

[`legacy/2026-07-10-mes-task-union/`](legacy/2026-07-10-mes-task-union/) 保存重构前的 6 字段结果和 Navicat 手工测试记录。该结果缺少 `PACKAGE`，只能证明当时一次历史执行，不得证明当前 7 字段契约，不得晋升或标记为 latest。

[`legacy/2026-07-13-mes-task-union/`](legacy/2026-07-13-mes-task-union/) 保存旧 runner 的质量、基线对比和性能产物；其 CSV 同样只有 6 列，结论不能替代当前 7 字段复验。

[`legacy/2026-07-16-interface-document-samples/`](legacy/2026-07-16-interface-document-samples/) 保存从旧需求文档提取的 642 行历史样本及 PACKAGE 覆盖报告，只代表当时活跃样本，不是物料全集。
