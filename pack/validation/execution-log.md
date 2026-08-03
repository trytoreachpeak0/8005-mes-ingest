# MesIngest 工厂验证 — 执行日志

复制本模板到回传目录并改名为 `execution-log.md`。不得粘贴密码、完整连接字符串、真实主机名或已填 `appsettings.Local.json`。

## 基本信息

- run_id：
- experiment_id：`mes-ingest-factory-validation`
- 执行人：
- 工厂/逻辑环境（勿写真实 host）：
- 开始时间（含时区）：
- 完成时间（含时区）：
- 安装包 `VERSION.txt` 摘要：

## 探针

- Thin：ok / failed / 未执行 — 日志文件：
- Thick：ok / failed / skipped — 日志文件：
- 有效模式：Thin / Thick
- 脱敏错误摘要（若失败，勿含账号密码）：

## 轮询采样

| 轮次 | 开始/结束 | duration_ms | row_count | success | 备注 |
|------|-----------|-------------|-----------|---------|------|
| 1 | | | | | |
| 2 | | | | | |
| 3 | | | | | |

## 人工核验

- [ ] A/B/C 三个逻辑地点均有独立 run；请求耗时、行数、correlation id 已对照
- [ ] `dates-samples.tsv` 已逐 TASK_TYPE 与 MES 页面/客户 IT 对照：DATES=当前工序进入时间、STEP=下一工序
- [ ] Oracle DATES 的 UTC+08:00 解释与 Watch 实际系统时区显示正确
- [ ] Host 日志含 Oracle round、SQL Server read/write、Host endpoint 分阶段证据；Watch 日志含 total latency
- [ ] `/api/demands` 首/后续页、DemandId exact/prefix、alerts、poll-health、ChangeFeed/Bootstrap 均已采样
- [ ] Watch timeout 现场配置值已记录，错误指向真实 endpoint/stage，未用无限增大 timeout 规避
- [ ] Swagger + SharedSecret 已实际执行只读 GET
- [ ] 原始快照行数（探针/`poll-health`）与 VISIBLE 条数对照，并抽查 TASK_TYPE+SUBLOT
- [ ] 告警已查看（失败 / 漂移 / 重复键 / PAUSED_ZERO_DROP / 重现）
- [ ] WPF 列表与横幅符合预期
- [ ] 关闭 WPF 后 Service 与 `GET /api/poll-health` 仍工作

偏差与说明：

| 逻辑地点 | Watch timeout(s) | Oracle/SQL/Host/Watch 证据完整 | 外部瓶颈/缺项 |
|----------|------------------|-------------------------------|-------------|
| A | | | |
| B | | | |
| C | | | |

## 脱敏自检

- [ ] 回传目录无 `appsettings.Local.json` / `config.ini` / 含密码文件
- [ ] 日志与 JSON 中无明文密码或完整连接串
- [ ] manifest `redaction.contains_secret_material=false`

## 导入记录（仓库侧填写）

- 回传人/时间：
- 接收人/时间：
- 落地路径：`mes/evidence/runs/<run_id>/`
- 是否误用 `meslab import-run`：否（本实验禁止）
- 脱敏复查：通过 / 隔离 / 拒绝
- 对照备注（与 samples / File 源）：

## 结论

- 验证包执行：完成 / 部分完成 / 失败
- 工厂签字（见 `signoff.md`）：有 / 无
- 限制与后续行动：
