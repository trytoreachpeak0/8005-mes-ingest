# MesIngest 工厂验证计划

experiment_id：`mes-ingest-factory-validation`

## 目的

在工厂机验证 MesIngest Windows Service 能连通 MES Oracle、持续单飞轮询、投影 TransportDemand，并经只读 HTTP / 可选 WPF 人工核验；建立脱敏回传与仓库对照闭环。

## 关联问题

- Thin 探针是否足够；失败时 Thick + Instant Client 是否可恢复。
- 多轮 `poll-health` 的耗时、行数、成败是否可观测。
- VISIBLE 投影与告警是否与现场快照感一致；WPF 横幅是否避免空板误判。
- 关闭 WPF 后 Service 与 HTTP 是否仍运行。

## 明确不回答

- 不宣称「工厂已签字通过」仅因验证包已进安装目录。
- 不证明派车/装载/运送状态机。
- 不修改客户 MES SQL、生产数据或账号权限。
- 不把回传晋升为 `mes/samples` latest（那是 meslab SQL 样本通道）。

## 输入

- 由 `pack/Publish-MesIngest.ps1` 生成的安装目录（含 `FACTORY-VALIDATION.md`、`validation/`、`INSTALL.md`、`VERSION.txt`）。
- 工厂机本地填写的 `service/appsettings.Local.json`（永不回传）。
- 本计划与清单：[`../../../ingest/csharp/pack/FACTORY-VALIDATION.md`](../../../ingest/csharp/pack/FACTORY-VALIDATION.md)。

## 安全边界

- 只读 Oracle 查询；凭证仅存工厂机本地配置。
- 回传禁止密码、已填 Local 配置、完整连接串。
- 探针错误输出依赖 Host 脱敏；仍须人工复查。

## 步骤

1. 工厂按 `FACTORY-VALIDATION.md` 填配置 → Thin 探针 → 必要时 Thick → 装服务。
2. 在 A/B/C 每个逻辑地点运行 `validation/Invoke-FactoryValidation.ps1`，自动采集分页 API、correlation id、Oracle/SQL/Host/Watch 分阶段耗时、DATES 样本和 SHA-256。
3. 人工逐 TASK_TYPE 对照 MES 页面/客户 IT 的 DATES/STEP 语义，核验 UTC+08:00→Watch 本机时区显示、Swagger+SharedSecret、timeout endpoint/stage，并填写 `dates-samples.tsv` / execution-log。
4. 按 `validation/RETURN-CHECKLIST.md` 组包；复核自动生成的 `run-manifest.json`、`request-metrics.jsonl` 与 `sha256.txt`。
5. 拷回开发机；**手工**导入到 `mes/evidence/runs/<run_id>/`（见下方「仓库导入」）。
6. 维护者 / Agent 对照 API JSON 与既有 `mes/samples/mes-task-union` 或 File 源做人工分析（字段形态、行数量级、告警类型）；结论写入该 run 的 execution-log「导入记录」。

## 仓库导入（复用 evidence 习惯，不用 import-run）

1. 确认回传包无 `appsettings.Local.json`、无密码字段。
2. 若目标 `mes/evidence/runs/<run_id>/` 已存在则**拒绝覆盖**，要求新 `run_id`。
3. 将整个回传目录复制为 `mes/evidence/runs/<run_id>/`（至少含 `run-manifest.json`、`execution-log.md`、探针日志、API JSON、`VERSION.txt`）。
4. 在 execution-log「导入记录」填写接收人、时间、脱敏复查结论。
5. **禁止** `python mes/tools/mes_lab.py import-run`（SQL bundle 契约；不得晋升 samples）。

证据总则见 [`../../README.md`](../../README.md) 与 [`../../../evidence/README.md`](../../../evidence/README.md)。

## 通过标准（技术证据）

- Thin 或 Thick 探针至少一次 `probe_result=ok`（或失败证据完整且已停止装服）。
- A/B/C 各有独立 run，request metrics 覆盖票据 15 指定 API 路径并含耗时/行数/correlation id。
- Oracle/SQL Server/Host/Watch 分阶段证据齐全，或权限/外部瓶颈有明确记录。
- `dates-samples.tsv` 逐 TASK_TYPE 完成人工语义及时区确认。
- Swagger+SharedSecret 与 30 秒可配置 timeout 的 endpoint/stage 提示已核验。
- 回传已脱敏；落地路径在 `evidence/runs/`。

## 通过标准（工厂签字）

- `signoff.md` 由现场填写「通过 / 有条件通过」——**可选**；缺失不阻塞本地开发。

## 结论模板

- run_id：
- 安装版本：
- 探针：Thin/Thick / 失败
- 多轮轮询：通过 / 不通过
- 投影与告警核验：通过 / 有条件 / 不通过
- WPF 与关窗后 Service：通过 / 不通过 / 未做 WPF
- 工厂签字：有 / 无
- evidence 路径：`mes/evidence/runs/<run_id>/`
- 后续行动：
