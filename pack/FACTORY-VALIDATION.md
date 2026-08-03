# MesIngest 工厂验证执行清单

本清单随安装包发布，用于工厂机验证 **MesIngest Service + Oracle 探针 + SQL Server + 远程 WPF**。
现场执行由人工完成；**本仓库交付「验证包已就绪」不等于「工厂已签字通过」**。

与 meslab `MES_TASK_UNION` SQL 实验清单（`mes/docs/工厂首轮执行与回传清单.md`）分开：本清单不跑 Python runner。

回传模板见同包 `validation/`；仓库导入说明见 `mes/experiments/definitions/mes-ingest-factory-validation/plan.md` 与 `mes/evidence/README.md`。

`validation/Invoke-FactoryValidation.ps1` 已把可机械执行的 GET、分页、计时、correlation id、证据落盘和哈希计算自动化。人工只需完成两类不能从系统自行推断的判断：逐 TASK_TYPE 对照 MES 页面/客户 IT 的 DATES/STEP 语义，以及 Watch 实际本机时区显示/30 秒 timeout 提示是否正确。

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
- [ ] 远程调用需鉴权时，把 SharedSecret 放入执行进程环境变量 `MES_INGEST_SHARED_SECRET`；不要作为脚本参数或命令历史明文传入

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

## 四、启动 Service 并运行 A/B/C 采集器

```powershell
# 安装根目录、管理员
.\scripts\install-service.ps1
Start-Service MesIngest
```

等待至少 **3** 轮完整轮询（默认轮间延迟约 10s）。然后在 A、B、C 三个逻辑地点各运行一次；`BaseUrl` 可以是真实地址，但产物只保存 URL path 和逻辑地点，不保存 BaseUrl。运行采集器期间保持该地点的 Watch 打开：

```powershell
cd <安装根>
.\validation\Invoke-FactoryValidation.ps1 -LogicalSite A -BaseUrl "https://<HOST>:<PORT>" -OutputRoot "C:\mes-ingest-runs"
# 分别在 B、C 地点改为 -LogicalSite B / C 后执行。
# 若脚本不在 Host 机上且有远程事件日志读取权限，可加：
# -HostEventComputerName "<HOST>"
```

- [ ] 每个 run 都有 `run-manifest.json`、`request-metrics.jsonl`、`dates-samples.tsv`、`host-latency.log`、`watch-latency.log`、`sha256.txt` 和 `api/`
- [ ] 采集器默认间隔 12 秒保存 3 轮 `poll-health`；只在排障复跑时显式调整 `-PollSampleCount` / `-PollSampleIntervalSeconds`
- [ ] `request-metrics.jsonl` 含 `/api/demands` 首/后续页、DemandId exact/prefix、alerts、poll-health、ChangeFeed/Bootstrap 的路径、耗时、行数、状态码和 correlation id
- [ ] `host-latency.log` 含 `ORACLE_QUERY` 及 `SQL_QUERY`/`SQL_WRITE`；远程事件日志无权限时，在 `execution-log.md` 记录并由 Host 机补采
- [ ] `run-manifest.json.status=technical-capture-completed`；若为 `technical-capture-incomplete`，按 `latency_evidence.missing_required_evidence` 补采，不得签字放行
- [ ] A/B/C 三个 run 均完成；不得用同一地点重复执行冒充三个链路
- [ ] 复制安装根 `VERSION.txt` 到回传目录

## 五、人工核验（原始快照行 vs 投影 vs WPF）

探针成功时，stdout 会打印本轮查询行数（及 `probe_result=ok`）。将该**原始快照行数/抽样键**与投影对照：

- [ ] **每个 TASK_TYPE 的 DATES/STEP**：打开自动生成的 `dates-samples.tsv`，每类至少一条与 MES 页面/客户 IT 对照；确认 `DATES=进入当前工序时间`、`STEP=下一工序`，填写四个 `PENDING` 列和证据引用
- [ ] **UTC+08:00 与 Watch 本机显示**：确认无 offset Oracle DATES 按 UTC+08:00 解释，Watch 按运行电脑实际系统时区显示 `yyyy-MM-dd HH:mm:ss zzz`
- [ ] **原始快照行 vs VISIBLE**：记录探针（或首轮成功 `poll-health`）的 `row_count`；与采集器遍历的 VISIBLE 总数对照（允许因上线基线过滤等略少，差异须写入 execution-log）
- [ ] **告警**：打开 `GET /api/alerts`（或落盘 JSON）；有查询失败 / 字段漂移 / 重复键 / `PAUSED_ZERO_DROP` / 重现时记录到 `execution-log.md`
- [ ] **WPF**：启动 `watch\MesIngest.Watch.exe`；确认列表展示 VISIBLE（及 GONE 若有）、告警与最近轮询健康
- [ ] **横幅**：若故意断 Oracle 或存在 `PAUSED_ZERO_DROP`，确认失败 / `PAUSED_ZERO_DROP` 横幅醒目，空板不会被误认为“无任务”
- [ ] **timeout 归因**：确认 Watch 配置为 30 秒（或明确记录现场值），错误显示真实 endpoint/stage；不得通过无限增大 timeout 判定通过
- [ ] **Swagger + SharedSecret**：浏览器打开 `/swagger`，Authorize 后至少实际执行一个 GET；不得增加或调用写接口

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
- `GET /api/demand-changes`

本验证禁止 Oracle DDL、索引、视图、SQL 重写和任何 HTTP 写方法。若证据显示 `stage=ORACLE_QUERY` 慢，只把脱敏证据交客户 IT。
