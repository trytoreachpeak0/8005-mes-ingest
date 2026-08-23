# MesIngest 工厂安装说明

自包含安装目录（由 `pack/Publish-MesIngest.ps1` 生成）可整包拷到工厂机。默认只读 HTTP 仅监听本机；开放非本机访问时必须配置共享密钥。

## 目录结构

```
MesIngest/
  service/                 # Windows Service（MesIngest.Host）自包含发布
    queries/mes-task-union/query.sql  # 唯一正式 Oracle 查询稿
    queries/mes-task-union/query.manifest.json # 查询版本、长度与原始 SHA-256
  watch/                   # 可选 WPF 盯盘客户端（MesIngest.Watch）
  openapi/v2.json          # Production V2 唯一 canonical OpenAPI 契约
  templates/               # 填空配置模板（无真实凭证）
  scripts/                 # 安装 / 卸载辅助脚本
  validation/              # 工厂验证、发布烟测与四套 Watch 验收入口
  INSTALL.md               # 本说明
  UPGRADE.md               # 现有安装升级、备份前置与回滚
  FACTORY-VALIDATION.md    # 工厂执行与核验清单
  VERSION.txt              # 发布版本信息
  RELEASE-MANIFEST.json    # 源提交、Production V2 发布面声明及逐文件 SHA-256
  RELEASE-EVIDENCE.json    # Ticket 11/12/13 重建代次与证据索引
```

## 配置（凭证不进包）

1. 复制 `templates/appsettings.Local.json.example` → `service/appsettings.Local.json`
2. 仅做独立 Watch 验收时：复制 `templates/watch.appsettings.Local.json.example` → `watch/appsettings.Local.json`（或直接改 `watch/appsettings.json`）
3. 填写 SQL Server、Oracle 等占位符；**不要**把填好的文件拷回仓库或再打进安装包
4. 默认 `Urls` 为 `http://127.0.0.1:5088`（仅本机）
5. 若改为非本机绑定（如 `http://0.0.0.0:5088` 或局域网 IP），必须同时设置 `SharedSecret`；调用方携带：

   `Authorization: Bearer <SharedSecret>`

6. WPF 非本机访问时，优先通过受保护的环境变量 `MesIngestWatch__SharedSecret` 注入同一密钥；示例文件保持空值。Watch 的 `external-configuration` 偏好只记录凭据来源，不保存密钥值
7. Watch 只配置一个 `BaseUrl`；请求超时合法范围为 1–300 秒，连接日志保留和 `SoftwareOnly` 渲染默认值见模板注释。获准的窗口、刷新和连接偏好写入 `%LocalAppData%\MesIngest.Watch`，不保存业务列表、查询、cursor 或凭据值

现有安装升级、SQL 备份与回滚见同目录 `UPGRADE.md`。

## Windows Service 安装 / 启停 / 卸载

在**管理员** PowerShell 中，于安装根目录执行：

```powershell
# 安装（可改服务名 / 显示名）
.\scripts\install-service.ps1

# 启动 / 停止
Start-Service MesIngest
Stop-Service MesIngest

# 卸载
.\scripts\uninstall-service.ps1
```

等价 `sc.exe`：

```powershell
sc.exe create MesIngest binPath= "C:\path\to\MesIngest\service\MesIngest.Host.exe" start= auto
sc.exe start MesIngest
sc.exe stop MesIngest
sc.exe delete MesIngest
```

安装前请先写好 `service/appsettings.Local.json`。Host 以可执行文件目录为 ContentRoot，因此即使服务进程工作目录是 `System32`，也会从 `service/` 读取配置与 `queries/`。建议用 `MesIngest.Host.exe --probe-oracle`（在 `service/` 目录、`SnapshotSource=Oracle`）做一次连通探针，成功后再装服务。

## 打包 Watch

打包的 `watch\MesIngest.Watch.exe` 与 Service 使用同一套冻结 V2 契约：契约发现、DemandSeries、资格审计、错误检索、当前接入关注和概览都走 `/api/v2/*`，版本或能力集合不精确匹配时 Watch 拒绝解释业务数据。它是只读客户端，从不写入投影。

Watch 与 Service 的进程生命周期互相独立：**关闭 WPF 不会停止 Service**，Service 继续轮询并继续提供 API。发布烟测会实测这一点（见下）。

## 日志位置

- **Windows Service**：写入 Windows **应用程序**事件日志（来源通常为 `.NET Runtime` / 进程名）；用「事件查看器」筛选 MesIngest.Host
- **控制台运行**：日志输出到当前终端（stdout/stderr）
- **Watch 连接事件**：本机 `%LocalAppData%\MesIngest.Watch\logs\` 按日 JSONL（首次失败 / 每 5 分钟摘要 / 恢复）；默认保留 30 天或 100 MB（先到先清理）。可用 `Watch:ConnectionLogRetentionDays` / `Watch:ConnectionLogMaxSizeMb`（或 `MesIngestWatch__*`）调整；`MesIngestWatch__LogDirectory` 可改写日志目录
- **Watch 渲染**：默认 `Watch:RenderingMode=SoftwareOnly`，规避虚拟/远程显示驱动导致的空白客户区；仅在确认硬件渲染兼容时改为 `Auto`
- ASP.NET 默认级别见 `service/appsettings.json` 的 `Logging` 节；可按现场需要调高

## Watch HTTP 超时

默认 `Watch:RequestTimeoutSeconds=30`（合法范围 1–300）。环境变量 `MesIngestWatch__RequestTimeoutSeconds` 可覆盖。非法值会使 Watch 启动失败并指出配置键与值。断线时保留最后一次成功数据，不在同一次刷新内立即重试。

## 版本信息

见安装根目录 `VERSION.txt`（发布时间、目标 RID、源码提交与 dirty 标记）和 `RELEASE-MANIFEST.json`（逐文件 SHA-256，以及 `canonicalQuery` 和 `openApi` 中固定的路径、契约/Schema 版本与 SHA-256）。`openapi/v2.json` 是唯一 canonical OpenAPI；`openApiStatus` 必须是 `FROZEN`，已退役的文档不能作为契约证据。程序集版本也可在 `service\MesIngest.Host.exe` 文件属性中查看。

## 发布烟测

在已登录的交互式 Windows 会话，先由 SQL Server 管理员创建一个**专用、可丢弃且当前没有任何用户表**的烟测库，再从安装包而非源码启动 Production V2 Host。不要指向共享、旧版或生产业务库；脚本不会为你删库或清表：

```powershell
$env:MES_INGEST_RELEASE_SMOKE_SQLSERVER = '<dedicated empty database connection string>'
$env:MES_INGEST_RELEASE_SMOKE_EMPTY_DATABASE_CONFIRMED = 'YES'
.\validation\Invoke-ReleaseSmoke.ps1 -ArtifactsDirectory C:\MesIngest\release-smoke
```

该入口强制 `DOTNET_ENVIRONMENT=Production`，把专用环境变量只注入进程内的 `MesIngest:NewSqlServerConnectionString`，以正式 `service\MesIngest.Host.exe` 建立/校验 V2 schema。它逐项验证：

- `GET /api/v2/contract` 的版本、schema、精确能力集合与只读策略严格匹配冻结契约；
- 包内 canonical `openapi/v2.json` 的 SHA-256 匹配发布清单，且运行时 `/openapi/v2.json` 与包内文档完整 JSON 语义严格一致；
- 退役面 `/api/contract`、`/api/demands`（含详情）、`/api/alerts`、`/api/poll-health`、`/api/demand-changes`、`/openapi/v1.json` 全部为 404；同时脚本会故意注入一个已退役配置键，Host 必须拒绝启动而不是忽略它，否则不接受 ADR-mes-0017 切换声明；
- 唯一 canonical Oracle 查询原稿与相邻 `query.manifest.json` 的路径、长度和 SHA-256；
- `GET /api/v2/externally-readable-demand-catalog` 首次返回非空正文、已提交的 `catalogRevision` 与对应弱 ETag；带同一 `If-None-Match` 的条件读取返回 304 且无正文；
- 受限原始证据 `/api/v2/error-search/{seriesId}/evidence/{evidenceId}/raw-observations` 即使在本机也必须带正确 Bearer 密钥：缺密钥或密钥错误一律 403；
- 非本机绑定且未配置 `SharedSecret` 时 Host 拒绝启动；
- Service 自己拥有轮询：`snapshot.pollTraceHighWater` 在没有任何 Watch 进程时持续前进；
- SQL Server 重启持久化：Host 被强行结束再启动后，目录条目集合与 `count` 完全一致、`catalogRevision` 不回退、`projectionCommitId` 仍存在；重启后的 Host 不轮询，两次读取返回同一 ETag，证明比对的是已落库投影而不是移动中的快照。

烟测用脚本生成的轮次录制驱动**同一条生产入口**（canonical 查询原稿、`OracleMesTaskUnionRoundSource`、ProjectionCommit 边界都是发布态代码），因此**无工厂 Oracle 也可重复执行**。录制只替换 Oracle 语句结果：Host 报告 driver `FILE_REPLAY`，`--probe-oracle` 在配置了录制时直接拒绝执行。**录制轮次永远不是工厂验收证据**，工厂 Oracle 验收仍按 `FACTORY-VALIDATION.md` 在现场完成。

烟测不写连接串，也不落盘可能含 SQL/provider 敏感信息的 Host stdout/stderr；产物 `release-smoke-result.json` 只记录脱敏后的状态码、哈希与计数。

### 打包 Watch 的启动耗时与进程独立性

默认不启动 WPF，`release-smoke-result.json` 记为具名 skip `PACKAGED_WATCH_PROCESS_INDEPENDENCE_AND_STARTUP_BUDGET`。在黄金机交互桌面加 `-IncludePackagedWatch` 才实际执行：启动包内 Watch → **实测进程启动到主窗口出现的耗时并对照上限** → 关闭窗口 → 确认 Service 未退出且 `pollTraceHighWater` 继续前进。

上限由 `-PackagedWatchStartupBudgetSeconds` 控制，默认 **25 秒**；超出即失败，实测毫秒数写入 `release-smoke-result.json` 的 `servicePollOwnership.watchIndependence.startupToMainWindowMs`。

```powershell
.\validation\Invoke-ReleaseSmoke.ps1 -IncludePackagedWatch -ArtifactsDirectory C:\MesIngest\release-smoke
```

该开关要求交互式会话，非交互会话直接报错而不是静默跳过。

## 打包 Watch 验收

正式 Windows 验收由独立测试仓提供，避免把 fake Host、xUnit、视觉基线或候选文件装进生产包。把 `-HarnessRoot` 指向同源码提交的 `mes\ingest\csharp`，入口会强制真实窗口套件启动本包内的 Watch：

```powershell
.\validation\Invoke-WatchAcceptance.ps1 -HarnessRoot C:\src\mes\ingest\csharp
```

默认 `-Suite watch-production-preview`，即非像素的 `watch-vm-tests` + `watch-ui-journeys`。**打包验收只证明已发布二进制的启动、连接与关键功能**，不重复像素候选、连续稳定计数、基线提升或 DPI clone —— 这些已在票 23 完成并获批。只有当打包差异真的改变了 PNG/XML/UIA/DPI 输出时，才使相应票 23 场景失效，并只用 `-Suite watch-window-visual` 重跑受影响门禁、重新取得批准。

## 只读运行反馈快照

当需要区分 Host 未监听、SQL Server 不可用、当前读取超时和契约不匹配时，从安装根目录运行：

```powershell
.\validation\Invoke-RuntimeFeedbackLoop.ps1 `
  -InstallRoot $PWD `
  -OutputRoot C:\MesIngestEvidence\runtime-feedback
```

入口只读取服务、进程、监听端点、部署清单/程序集、V2 GET、SQL Server DMV/错误信号和可选
TRX；不会启动或停止服务、修改数据库或写生产配置，也不会把凭据或连接字符串写入证据。
若从源码工作区运行，可额外传入 `-RepositoryRoot <repo>` 记录所有脏文件并按“本票 / 优化重叠 /
无关”分类；这些条目一律保持未归属，不会被清理或纳入本票。

真实 SQL Tier 1 使用 VSTest/xUnit v2 命令（从 `mes/ingest/csharp`）：

```powershell
$env:MES_INGEST_TICKET01_SQLSERVER = '<approved real SQL Server master connection>'
$env:MES_INGEST_TICKET01_EXPECTED_PRODUCT_MAJOR = '16'
$env:MES_INGEST_TICKET01_EXPECTED_COMPATIBILITY_LEVEL = '160'
.\Invoke-RuntimeFeedbackTier1.ps1 `
  -ExpectedProductMajor 16 `
  -ExpectedCompatibilityLevel 160
```

源码入口执行固定的 `dotnet test MesIngest.Tests` VSTest 命令并生成 TRX 与脱敏 attestation。随后在
同一环境中把两者分别以 `-SqlTestTrxPath` 与 `-SqlTestAttestationPath` 传给收集器；只有
`Failed=0`、`Skipped=0`、`Total>=700`、目标不是 LocalDB，并且新鲜 attestation 中的命令、
程序集、TRX SHA-256、计数、SQL 目标和实际版本全部匹配时，报告才写
`realSqlTier1Satisfied=true`。

## 基本故障排查

| 现象 | 检查 |
|------|------|
| Service 无法启动 | `service/appsettings.Local.json` 是否存在；`Urls` 非本机时是否设置了 `SharedSecret`；事件查看器中的异常 |
| API 401 | 非本机绑定时是否带了 `Authorization: Bearer …`，密钥是否与配置一致 |
| 空板 / 无需求 | `GET /api/v2/current-ingest-attention`、`GET /api/v2/demand-series`；Oracle 探针是否成功 |
| Oracle 连不上 | `OracleMode` Thin→Thick，并同时配置 `OracleInstantClientDir` + 已注册 `OracleThickOdbcDriver`；账号/数据源；工厂网络。Thick 不会回退 Thin |
| SQL Server 投影丢失 | `NewSqlServerConnectionString`；库是否可连；Production 不允许空连接串或内存回退 |
| 查询 SQL 缺失或被拒绝 | 只确认 `service/queries/mes-task-union/query.sql` 与邻接 `query.manifest.json`；不要从别处补第二份 SQL。二者必须匹配发布清单中的 `canonicalQuery` |

只读 API（本机默认）：

- `GET /api/v2/contract`
- `GET /api/v2/demand-series`
- `GET /api/v2/demand-series/{seriesId}`
- `GET /api/v2/current-ingest-attention`
- `GET /api/v2/watch-overview`
- `GET /api/v2/poll-traces/{pollTraceId}`

人工试调与合作者文档：

- V2 合约身份：`GET /api/v2/contract`；唯一 canonical 描述：包内 `openapi/v2.json`，运行时 `GET /openapi/v2.json`
- 已退役的 `/api/*` 与 `/openapi/v1.json` 不再由任何环境提供，也不属于能力发现、兼容面或发布面
- 实际 `/api/v2/*` 在非本机绑定时需 `Authorization: Bearer <SharedSecret>`；受限原始证据即使在本机也要求显式 Bearer 密钥

工厂连通验证、人工核验与回传约定见同包 [`FACTORY-VALIDATION.md`](FACTORY-VALIDATION.md) 与 `validation/`；本说明只覆盖安装与安全配置。
