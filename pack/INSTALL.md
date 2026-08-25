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

## StoragePressurePause 本地恢复

恢复只能在 SQL Server 数据库主机上以 Windows 集成身份运行打包的本地管理程序。先把精确连接串放入当前进程环境变量，不要放到命令行；再提供 Watch 诊断显示的精确数据库名、HistoryEpoch 和操作原因：

```powershell
$env:MES_INGEST_LOCAL_ADMINISTRATION_CONNECTION_STRING = '<integrated-security connection string>'
.\administration\MesIngest.LocalAdministration.exe resume-storage-pressure `
  --database '<exact database name>' `
  --history-epoch '<current HistoryEpoch>' `
  --reason '<operator reason>'
Remove-Item Env:MES_INGEST_LOCAL_ADMINISTRATION_CONNECTION_STRING
```

命令会再次验证执行位置、`db_owner`/`sysadmin` 授权、精确数据库和纪元、当前暂停状态、卷空间至少 15%、数据库 `ONLINE`/`READ_WRITE`，并在同一事务中写恢复审计。Watch 与远程 Host API 没有恢复写操作；直接修改状态表不会形成合法恢复审计，也不会重新开放轮询。

## 日志位置

- **Windows Service**：写入 Windows **应用程序**事件日志（来源通常为 `.NET Runtime` / 进程名）；用「事件查看器」筛选 MesIngest.Host
- **控制台运行**：日志输出到当前终端（stdout/stderr）
- **Watch 连接事件**：本机 `%LocalAppData%\MesIngest.Watch\logs\` 按日 JSONL（首次失败 / 每 5 分钟摘要 / 恢复）；默认保留 15 天或 100 MB（先到先清理）。可用 `Watch:ConnectionLogRetentionDays` / `Watch:ConnectionLogMaxSizeMb`（或 `MesIngestWatch__*`）调整；`MesIngestWatch__LogDirectory` 可改写日志目录
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

## SQL Server 低内存运行配置

包内唯一受支持入口为
`scripts/maintenance/Invoke-SqlServerMemoryProfile.ps1`。常态只允许 1536 MB，维护期只允许
2048 MB；800 MB 及更低值属于已知不可运行配置，校验会在连接 SQL Server 前拒绝，禁止再次用生产
负载重现 Error 701。

入口只从进程环境变量读取管理员连接，且该连接必须显式指向 `master`。每次写操作还要同时给出
SQL Server 实际 MachineName、InstanceName 和完全相同的 `MachineName\InstanceName` 确认文本；
LocalDB、错误实例、缺少 `ALTER SETTINGS` / `VIEW SERVER STATE` 权限或读取失败都会非零退出。

应用或重新确认常态配置：

```powershell
$env:MES_INGEST_SQLSERVER_ADMIN = '<approved SQL Server master connection>'
.\scripts\maintenance\Invoke-SqlServerMemoryProfile.ps1 `
  -Action ApplyNormal `
  -RequestedMaxServerMemoryMb 1536 `
  -ExpectedMachineName LAB-WIN-01 `
  -ExpectedInstanceName MSSQLSERVER `
  -ConfirmInstance 'LAB-WIN-01\MSSQLSERVER'
```

只读诊断使用相同目标参数并改为 `-Action Diagnose`。JSON/Markdown 证据默认写入
`%ProgramData%\MesIngest\evidence\sql-memory-profile`，包含 SQL Server committed memory、
workspace memory、grant 等待、RESOURCE_SEMAPHORE、Error 701、spill，以及默认三次
`sqlservr` 物理内存采样对约 2 GB 目标的判定；不写连接字符串、账号密码或 SQL 错误日志原文。

受控维护必须把实际维护动作放在一个明确的 `.ps1` 文件中，并由包装器执行：

```powershell
.\scripts\maintenance\Invoke-SqlServerMemoryProfile.ps1 `
  -Action RunMaintenance `
  -RequestedMaxServerMemoryMb 2048 `
  -ExpectedMachineName LAB-WIN-01 `
  -ExpectedInstanceName MSSQLSERVER `
  -ConfirmInstance 'LAB-WIN-01\MSSQLSERVER' `
  -Reason 'planned index maintenance CHG-1234' `
  -MaintenanceScriptPath C:\MesIngestMaintenance\Invoke-PlannedWork.ps1
```

包装器只从已验证的 1536 MB 常态进入 2048 MB，并在子脚本成功、失败、超时或以 Windows
取消码 1223 退出后，在 `finally` 中恢复并复核 1536 MB。报告记录 Windows 操作者、原因和 UTC
起止时间。恢复失败时状态为 `RESTORE_FAILED` 并非零退出，绝不把部分应用报告为成功；此时停止
后续维护并立即用 `ApplyNormal` 处置。不要用任务管理器或 `Stop-Process` 强杀包装器，因为操作系统
强制终止无法执行任何进程内 `finally`。

## 规模数据与查询证据门禁

`validation/Invoke-ScaleAndQueryEvidence.ps1` 是独立的破坏性验证入口，不属于日常 Host。它只接受
指向 `master` 的真实 SQL Server 连接和一个尚不存在、名称匹配 `MesIngest_Scale_*` 的显式目标；
系统库、LocalDB、现有库、`MesIngest_V2` 和无法证明所有权的库都会在删除前被拒绝。运行账号需要
`CREATE ANY DATABASE` 与 `ALTER ANY EVENT SESSION`。先准备第 1 票生成的 Skipped=0 Tier 1
attestation，再执行例如 7 天 profile：

```powershell
$env:MES_INGEST_SCALE_EVIDENCE_SQLSERVER = '<approved real SQL Server master connection>'
.\validation\Invoke-ScaleAndQueryEvidence.ps1 `
  -ProfileDays 7 `
  -DatabaseName MesIngest_Scale_Ticket02_7Day `
  -ConfirmIsolatedDatabase MESINGEST_SCALE_EVIDENCE_ONLY `
  -SqlTier1AttestationPath C:\MesIngestEvidence\runtime-feedback-tier1-attestation.json `
  -OutputRoot C:\MesIngestEvidence\scale-query
```

可选 profile 只有 0、7、15。默认分布固定为每 14 秒 600 条观测、600 个 Series、70% 活跃、
30% 归档、10% 活动错误；数据种子、锚点时间、查询参数、构建、contract/schema 和 SQL Server
身份都会写入重放清单。工具通过包内生产 Host 的唯一 V2 API 驱动 DemandSeries、外部目录、当前关注、
Overview、ReadabilityAudit、ErrorSearch 与原始证据，并以 Extended Events 记录每个查询面的实际计划、
逻辑读、CPU/耗时、内存授予和 spill。存储报告分别列出逻辑已用空间、MDF/NDF、LDF、表、聚集索引、
非聚集索引和压缩状态。

Tier 1 attestation 必须使用 schema 2：工具会复核 `exitCode`、TRX SHA-256、TRX 内实际计数、
SQL 实例/版本，并要求 `sourceCommit` 与 Host DLL SHA-256 精确匹配本次规模运行。旧构建、手改计数、
缺失 TRX 或同实例上的陈旧 attestation 均不能使门禁变绿。

默认成功或失败后都会验证数据库扩展属性中的精确 run ID，再删除本次创建的库；`-KeepDatabase` 只供
人工故障调查。缺少任一查询面的实际计划/statement 指标、空数据、未知构建身份，或 Tier 1
`Skipped` 非 0 时，仍会保存证据但门禁失败。7/15 天 profile 会写入约 2592 万/5554 万条原始观测，
应预留足够时间与隔离磁盘；日常实现或 Tier 1 不会自动运行它们。

Ticket 27 的快速容量路径复用同一入口和固定分布，不创建第二套容量工具。先以
`-FastCapacityProjection` 生成 0-history baseline，再以相同包、SQL 实例、查询参数和 Host SHA
运行最多 415 个代表性历史轮次；415 轮加当前轮共 249,600 条 RawObservation，入口会在连接 SQL
前拒绝更大的样本：

```powershell
$baseline = 'C:\MesIngestEvidence\capacity\<baseline-run>\scale-query-evidence.json'
.\validation\Invoke-ScaleAndQueryEvidence.ps1 `
  -ProfileDays 0 `
  -DatabaseName MesIngest_Scale_Ticket27_Sample `
  -ConfirmIsolatedDatabase MESINGEST_SCALE_EVIDENCE_ONLY `
  -FastCapacityProjection `
  -RepresentativeHistoryRounds 415 `
  -BaselineEvidencePath $baseline `
  -QuerySurface DemandSeries `
  -RoundBatchSize 100 `
  -OutputRoot C:\MesIngestEvidence\capacity
```

容量报告按表/聚集索引/非聚集索引列出 PAGE 压缩实测，记录每轮、每行、墓碑、版本存储、tempdb、
LDF、固定 MB 自动增长、SIMPLE recovery、`log_reuse_wait_desc`、1536 MB max server memory 和发布包
清理默认值。15 天模型使用 `floor(15*86400/14)=92571` 轮、55,542,600 行，并对预测增加 30%
余量；墓碑按固定归档 Series 数量计入逻辑总量，version-store/tempdb 按实测峰值加 30% 报告。
物理文件预测保留实测起始大小，再按固定增长量向上取整。逻辑已用、物理数据文件和 LDF 分别以
12,288/16,384/2,048 MB 为最终硬上限；达到 70% 或增长段速率比超过 1.20 会如实记录 advisory
warning，但不阻断。只有预测达到硬上限，或样本、PAGE 压缩、清理完整性等关键证据不确定时才
fail closed。快速容量运行只在开发期间延后最终 Tier 1 绑定；关闭 ticket 时仍必须在同一真实 SQL
Server 上完成一次 `Failed=0 / Skipped=0` Tier 1。

Ticket 28 的加速并发稳定性路径继续复用同一入口、同一固定 600 Series 分布、发布 Host、
录制 `MES_TASK_UNION` 轮次、V2 HTTP 客户端、冻结读取、清理和 SQL 诊断，不创建第二套 soak
平台。先为同一候选生成 0-history 基线并保留 JSON；再以不超过 250,000 条 RawObservation 的
代表性历史运行 30–45 分钟。运行前把可控时间契约测试的 TRX 传给门禁：

```powershell
$package = 'C:\MesIngestCandidate'
$contractOutput = .\Invoke-Ticket28DeterministicContract.ps1 `
  -PackageRoot $package `
  -ResultsRoot C:\MesIngestEvidence\ticket28-contract
$contractOutput
$deterministicAttestation = [string]($contractOutput | Where-Object { $_ -like 'ATTESTATION=*' })
$deterministicAttestation = $deterministicAttestation.Substring('ATTESTATION='.Length)

$env:MES_INGEST_SCALE_EVIDENCE_SQLSERVER = '<approved real SQL Server master connection>'
.\validation\Invoke-ScaleAndQueryEvidence.ps1 `
  -ProfileDays 0 `
  -DatabaseName MesIngest_Scale_Ticket28_Baseline `
  -ConfirmIsolatedDatabase MESINGEST_SCALE_EVIDENCE_ONLY `
  -FastCapacityProjection `
  -OutputRoot C:\MesIngestEvidence\ticket28

$baseline = 'C:\MesIngestEvidence\ticket28\<baseline-run>\scale-query-evidence.json'
.\validation\Invoke-ScaleAndQueryEvidence.ps1 `
  -ProfileDays 0 `
  -DatabaseName MesIngest_Scale_Ticket28_Stability `
  -ConfirmIsolatedDatabase MESINGEST_SCALE_EVIDENCE_ONLY `
  -AcceleratedConcurrencyStability `
  -StabilityDurationMinutes 30 `
  -RepresentativeHistoryRounds 100 `
  -BaselineEvidencePath $baseline `
  -DeterministicContractEvidencePath $deterministicAttestation `
  -CapacityEvidencePath C:\MesIngestEvidence\ticket27-15-day-retention\capacity-summary.json `
  -OutputRoot C:\MesIngestEvidence\ticket28
```

该 profile 只把轮询 start-to-start 间隔缩短到 1 秒、清理检查缩短到 60 秒并增加并发读取次数；
发布 `appsettings.json` 仍是 60 秒 start-to-start、60/120/300 秒失败退避和每小时清理，Watch
默认仍是 Overview/Current Attention 30 秒及 Demand Series/Readability Audit/Error Search 60 秒。
门禁按 API surface、Host 进程代次和稳定阶段分别计算 P95/P99 与首尾趋势；冻结一致性读取只允许
200，另以独立请求验证故意过期对象返回 410 `MES_INGEST_HISTORY_EXPIRED`，预期 410 不计入 HTTP
错误。RESOURCE_SEMAPHORE 同时记录实例累计上下文和按 MesIngest application name 归因的 session
增量/连续 active 或 pending grant；Host working set/handle 只评价同一进程代次后半程。LDF 同时记录
physical/used、autogrowth、`log_reuse_wait_desc`，并在最后一次固定 autogrowth 后的稳定窗判定 used
斜率与是否平台化。spill 必须包含 API surface、query hash、query-plan hash、operator/node 和内存/写盘
计数，任何实际 spill 都失败。门禁还连续采集数据库/tempdb/version store、SQL 内存、Error 701、锁
等待、清理、earliest available、StoragePressurePause、重启与趋势。任一真实风险或缺证都会以具名
`STABILITY_*` 失败退出并要求 4 小时或 24 小时真实 soak；正常快速路径仍需在关闭 Ticket 时另行
绑定唯一一次 Skipped=0 的真实 SQL Server Tier 1。

`-StabilityDurationMinutes` 只接受快速门禁的 30–45 分钟，以及升级验证的 240 或 1440 分钟；
240/1440 只用于快速门禁已出现可归因风险或用户明确要求的升级。门禁会从分钟级资源快照重新计算
真实持续时间、采样间隔，并要求最终 Host 代次的每个 API surface 都覆盖末段；只修改汇总时长或
缺少末段 phase 证据不能使长跑门禁通过。

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
