# MesIngest 工厂验证执行清单

本清单随安装包发布，用于工厂机人工验证 **MesIngest Service + Oracle 探针 + 可选 WPF**。
现场执行由人工完成；**本仓库交付「验证包已就绪」不等于「工厂已签字通过」**。

与 meslab `MES_TASK_UNION` SQL 实验清单（`mes/docs/工厂首轮执行与回传清单.md`）分开：本清单不跑 Python runner。

回传模板见同包 `validation/`；仓库导入说明见 `mes/experiments/definitions/mes-ingest-factory-validation/plan.md` 与 `mes/evidence/README.md`。

## 两态区分（必读）

| 状态 | 含义 | 谁确认 |
|------|------|--------|
| **验证包已就绪** | 安装目录含本清单与 `validation/` 模板；本地 publish/文档齐备 | 仓库维护者 / Agent |
| **工厂已签字通过** | 现场按本清单执行并完成 `validation/signoff.md` | 工厂执行人 / 客户方 |

工厂签字**不阻塞**本地后续开发分支。未签字前不得宣称“现场已通”。

## 建议回传目录（工厂机本地新建）

在安装根旁建唯一 `run_id` 目录（例：`mes-ingest-runs\run-20260727T050000Z-abcd1234`），后续探针日志与 API JSON 写入该目录。完成后按 `validation/RETURN-CHECKLIST.md` 打包回传。

---

## 一、填配置

- [ ] 复制 `templates/appsettings.Local.json.example` → `service/appsettings.Local.json`
- [ ] 填写 SQL Server、Oracle 占位符；`SnapshotSource=Oracle`；默认 `OracleMode=Thin`
- [ ] 确认 `Urls` 仍为 `http://127.0.0.1:5088`（或已按 `INSTALL.md` 配置 `SharedSecret`）
- [ ] **不要**把已填配置拷回仓库或放进回传包

## 二、Thin 探针

在 `service/` 目录执行：

```powershell
.\MesIngest.Host.exe --probe-oracle > ..\..\mes-ingest-runs\<run_id>\probe-thin.txt 2>&1
```

（将 `mes-ingest-runs\<run_id>` 换成你的回传目录；也可先在控制台看输出再手工保存。）

- [ ] 输出含 `probe_result=ok` → 进入「四、启动 Service」
- [ ] 若 `probe_result=failed` → 进入「三、Thick 重试」

## 三、失败则切 Thick 重试

- [ ] 将 `service/appsettings.Local.json` 中 `OracleMode` 改为 `Thick`
- [ ] 配置 Instant Client 路径（`OracleInstantClientDir` 或环境变量 `ORACLE_CLIENT_LIB_DIR`）
- [ ] 再次执行 `--probe-oracle`，保存为 `probe-thick.txt`
- [ ] Thick 仍失败：记录脱敏错误到 `execution-log.md`，**停止**装服务，回传失败证据即可

## 四、启动 Service 并观察多轮轮询

```powershell
# 安装根目录、管理员
.\scripts\install-service.ps1
Start-Service MesIngest
```

等待至少 **3** 轮完整轮询（默认轮间延迟约 10s）。对本机 API 采样（PowerShell）：

```powershell
$base = "http://127.0.0.1:5088"
$out = "C:\path\to\mes-ingest-runs\<run_id>\api"
New-Item -ItemType Directory -Force -Path $out | Out-Null
1..3 | ForEach-Object {
  $i = "{0:D2}" -f $_
  Start-Sleep -Seconds 12
  Invoke-RestMethod "$base/api/poll-health" | ConvertTo-Json -Depth 6 |
    Set-Content (Join-Path $out "poll-health-round-$i.json") -Encoding UTF8
  Invoke-RestMethod "$base/api/demands?status=VISIBLE" | ConvertTo-Json -Depth 8 |
    Set-Content (Join-Path $out "demands-visible-round-$i.json") -Encoding UTF8
  Invoke-RestMethod "$base/api/alerts" | ConvertTo-Json -Depth 6 |
    Set-Content (Join-Path $out "alerts-round-$i.json") -Encoding UTF8
}
```

- [ ] 多轮 `poll-health` 中可见开始/结束时间、耗时、行数、成功/失败
- [ ] 将每轮 `duration_ms` / `row_count` / `success` 填入 `validation/run-manifest.example.json` 副本（改名为 `run-manifest.json`）
- [ ] 复制安装根 `VERSION.txt` 到回传目录

## 五、人工核验（原始快照行 vs 投影 vs WPF）

探针成功时，stdout 会打印本轮查询行数（及 `probe_result=ok`）。将该**原始快照行数/抽样键**与投影对照：

- [ ] **原始快照行 vs VISIBLE**：记录探针（或首轮成功 `poll-health`）的 `row_count`；与 `GET /api/demands?status=VISIBLE` 条数对照（允许因上线基线过滤等略少，差异须写入 execution-log）。从 VISIBLE JSON 抽查至少 3 条 `TASK_TYPE`+`SUBLOT`，确认与现场对 MES 结果的预期一致（若工厂另有只读查询工具，可用同一键交叉核对）。
- [ ] **告警**：打开 `GET /api/alerts`（或落盘 JSON）；有查询失败 / 字段漂移 / 重复键 / `PAUSED_ZERO_DROP` / 重现时记录到 `execution-log.md`
- [ ] **WPF**：启动 `watch\MesIngest.Watch.exe`；确认列表展示 VISIBLE（及 GONE 若有）、告警与最近轮询健康
- [ ] **横幅**：若故意断 Oracle 或存在 `PAUSED_ZERO_DROP`，确认失败 / `PAUSED_ZERO_DROP` 横幅醒目，空板不会被误认为“无任务”

## 六、关闭 WPF 后 Service 仍工作

- [ ] 关闭 WPF 窗口
- [ ] `Get-Service MesIngest` 仍为 Running
- [ ] 再次 `Invoke-RestMethod http://127.0.0.1:5088/api/poll-health` 成功
- [ ] （可选）稍后重开 WPF，确认能重连并显示当前投影

## 七、回传前自检

按 `validation/RETURN-CHECKLIST.md` 勾选；填写 `execution-log.md`；若客户方同意验收则填 `signoff.md`（可空着只回传技术证据）。

## 只读 API 速查

- `GET /api/demands`（可 `?status=VISIBLE|GONE`）
- `GET /api/alerts`
- `GET /api/poll-health`
