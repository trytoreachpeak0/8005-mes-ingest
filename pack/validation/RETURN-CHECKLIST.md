# MesIngest 工厂验证 — 回传清单

回传前在工厂机勾选。目的：把**可分析、已脱敏**的证据交给仓库维护者 / Agent，而不是把整机配置拷回。

## 必含

- [ ] `run-manifest.json`（由 `run-manifest.example.json` 填写；含 `experiment_id=mes-ingest-factory-validation`）
- [ ] `execution-log.md`（脱敏后的执行与核验记录）
- [ ] 探针日志：`probe-thin.txt` 和/或 `probe-thick.txt`（Host 已脱敏；仍勿粘贴密码）
- [ ] 安装根 `VERSION.txt` 副本
- [ ] 至少 3 轮 API 落盘：每轮 `poll-health` / `demands?status=VISIBLE` / `alerts`（或等价文件名，并在 manifest 引用）
- [ ] 每轮在 manifest 中记录 **耗时**（`duration_ms`）、**行数**（`row_count`）、**成败**（`success` / `outcome`）
- [ ] 脱敏日志：至少含探针日志；也可附 API JSON 中的健康/告警摘要

## 禁止回传

- [ ] **禁止**已填写的 `appsettings.Local.json`（含密码或连接串）
- [ ] **禁止** `config.ini`、环境变量转储、密钥文件、完整连接字符串
- [ ] **禁止**把密码写进 manifest、execution-log、JSON 或聊天记录
- [ ] **禁止**覆盖仓库已有 `mes/evidence/runs/<run_id>/`（导入时新建唯一 run_id）

## 可选

- [ ] `signoff.md`（工厂签字；无签字仍可回传技术证据）
- [ ] 事件查看器中与 MesIngest 相关的脱敏摘录（写入 execution-log 即可）
- [ ] 额外轮次或故障复现附件（新文件名，勿改写已有产物）

## 拷回方式

文件夹拷贝即可。仓库侧按 `mes/evidence/README.md` 中 **MesIngest 工厂验证回传** 小节落地到 `mes/evidence/runs/<run_id>/`。

**不要**对 MesIngest 验证包运行 `python mes/tools/mes_lab.py import-run`（该命令面向 SQL query bundle，契约不匹配，且不得晋升 `samples/`）。
