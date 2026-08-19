# MesIngest 空库切换与整体回退说明

本说明随 `pack/Publish-MesIngest.ps1` 输出到安装根目录 `UPGRADE.md`。首次安装仍以 `INSTALL.md` 为准。

## 这不是就地升级

按 ADR-mes-0017，本版本以**空数据库整体替换**上线，不做就地升级：

- 不迁移、不转换、不推测任何历史业务数据；新历史从第一个完整成功 `MesTaskUnionRound` 开始；
- 不提供旧 schema 适配器、旧 API 适配器或新旧混跑；
- 数据库删除永远是**停机 + 备份 + 人工确认精确目标**之后的部署操作。Host、Watch、`install-service.ps1`、`uninstall-service.ps1` 都不会删除任何数据库，卸载服务也不会。

Host 只接受两种数据库状态：

| 目标库状态 | 行为 |
|---|---|
| 完全空库（0 张用户表） | 一次性建立完整 schema |
| 已精确匹配当前契约的库 | 严格校验后继续使用 |
| 其它（旧表、缺列、多表、版本不符） | 拒绝启动，不改造、不回退内存 |

配置层面同样不留后路：`MesIngest` 配置节若仍带有已退役键（`SqlServerConnectionString`、`SnapshotCsvPath`、`ChangeFeedRetentionHours`、`AlertRetentionDays`、`DisappearThreshold`、`GoLiveBaseline`、`EnableLegacyDevelopmentEndpoints`），Host 直接拒绝启动，而不是忽略它们。这样一份为旧契约写的部署文件不会看起来"被接受"。

## 切换前必做

1. **停止全部写入与读取方**

   ```powershell
   Stop-Service MesIngest
   # 退出全部 Watch 进程，并停止所有外部消费者
   ```

2. **准备备份落盘位置**

   备份由切换脚本执行并校验，落在 **SQL Server 主机**上——路径是那台机器的本地路径，不是运行脚本这台机器的路径。请预留独立于新库的磁盘位置并确认目录已存在，切换后长期保留，它是唯一的回退资产。

3. **备份当前安装根目录**

   保留完整旧包、`VERSION.txt`、`service/appsettings.Local.json` 以及 Watch 本机配置。已填密钥的 Local.json 不得回传仓库或复制进新发布包。

## 执行空库切换

`scripts/cutover/Invoke-EmptyDatabaseCutover.ps1` 是产品中**唯一**会删除数据库的入口。

```powershell
.\scripts\cutover\Invoke-EmptyDatabaseCutover.ps1 `
    -ConnectionString 'Server=<SQL_HOST>;Database=master;Integrated Security=True;TrustServerCertificate=True' `
    -DatabaseName 'MesIngest' `
    -BackupPath 'D:\backup\MesIngest_pre_cutover.bak' `
    -DowntimeAcknowledgement OLD_HOST_WATCH_AND_CONSUMERS_STOPPED `
    -EvidenceDirectory .\.cutover-evidence
```

脚本按顺序做以下事情，任一步失败即中止且不删除任何东西：

1. 连接必须走 `master`，不能直接连目标库；
2. 从连接本身解析真实 `MachineName\InstanceName` 和库名并打印；
3. 核对目标库上没有其它用户会话（否则说明还有旧 Host、Watch 或外部消费者没停）；
4. `BACKUP DATABASE ... WITH INIT, FORMAT, CHECKSUM`，再 `RESTORE VERIFYONLY ... WITH CHECKSUM`，并记录备份文件 SHA-256。备份是非破坏性的，放在确认之前，这样一次确认只授权删除动作，备份路径不可用时也不会先让人确认再失败；
5. **要求操作员在控制台原样键入 `实例/库名`**。没有任何开关可以跳过这一步，因此非交互或重定向输入的无人值守调用一定停在这里，不会对未确认实例执行 DROP；
6. `ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE` 后 `DROP DATABASE`；
7. 新建同名**空库**并核对用户表数为 0；
8. 写出 `cutover-evidence.json`：目标身份、备份路径与哈希、前后用户表数、操作员、时间。

脚本不会创建 MesIngest schema。接着从 `templates/appsettings.Local.json.example` 重新创建
`service/appsettings.Local.json`，不要覆盖式沿用旧文件，至少核对：

- `NewSqlServerConnectionString`：指向刚刚建立的空库；
- `SnapshotSource=Oracle`；
- `OracleUser`、`OraclePassword`、`OracleDataSource`；
- `OracleMode=Thin` 作为默认尝试；如需 Thick，还必须同时填 `OracleInstantClientDir` 和已注册的 `OracleThickOdbcDriver`；
- 跨机绑定时的 `Urls` 和 `SharedSecret`。

`service/queries/mes-task-union/query.sql` 是唯一正式 Oracle SQL，必须与相邻 `query.manifest.json`
及根目录 `RELEASE-MANIFEST.json` 一致；先在安装根目录执行 `.\scripts\Test-ReleasePackage.ps1 -PackageRoot .`
校验包内容，再在 `service/` 执行 `.\MesIngest.Host.exe --probe-oracle` 取得 `LIVE_ORACLE` 探针结果。

接着安装并启动新版 Host：

```powershell
.\scripts\install-service.ps1
Start-Service MesIngest
```

Host 首次启动在空库中一次建立完整 schema。随后用只读证据确认这是一段**新**历史：

- `GET /api/v2/contract` 返回冻结契约身份；
- `GET /api/v2/current-ingest-attention?pageNumber=1&pageSize=100`；
- 取到 PollTraceId 后 `GET /api/v2/poll-traces/{pollTraceId}`，核对 canonical query version、规范化 content digest、row count 与 outcome；
- 首个完整 SUCCESS 之后，错误当前条件以 `BOOTSTRAPPED_CURRENT_CONDITION` 起算，不伪造任何早于本次 bootstrap 的开始时间；
- 库中不存在任何来自已退役契约的 TransportDemand、IngestAlert 或 ChangeFeed 记录——这些退役表在新 schema 中根本不存在；
- 关闭/不启动 Watch 时 PollTrace high-water 仍继续前进。

建议按 `FACTORY-VALIDATION.md` 和 `validation/Invoke-FactoryValidation.ps1` 采集完整证据。

## 整体回退

回退不是新版内部的降级路径，而是把上一套部署整体恢复：

1. `Stop-Service MesIngest`，退出全部 Watch；
2. 恢复切换前备份的安装目录、旧版二进制与原 `service/appsettings.Local.json`；
3. 用独立备份恢复旧数据库：

   ```powershell
   .\scripts\cutover\Invoke-CutoverRollback.ps1 `
       -ConnectionString 'Server=<SQL_HOST>;Database=master;Integrated Security=True;TrustServerCertificate=True' `
       -DatabaseName 'MesIngest' `
       -BackupPath 'D:\backup\MesIngest_pre_cutover.bak' `
       -PreviousDeploymentRestored OLD_PROGRAMS_AND_OLD_CONFIGURATION_RESTORED `
       -EvidenceDirectory .\.cutover-evidence
   ```

   该脚本同样要求控制台原样键入目标身份；它先确认新版服务已停止或未安装，恢复后再核对恢复出来的库**不含**当前契约的 `mesingest` schema——否则说明恢复了错误的备份。

4. 启动旧版程序，只按它自身的契约做冒烟；
5. 保留失败的脱敏探针、Host 日志和 PollTrace 证据供调查；不得回传 SQL、连接串、凭据、datasource 或原始 MES 值。

新程序不读旧库，旧程序不读新库，也不存在滚动升级、双写或影子同步模式。

## 数据与只读承诺

| 对象 | 行为 |
|---|---|
| 客户 Oracle / 批准 `MES_TASK_UNION` SQL | 仅执行一条已校验的只读查询；不部署 Oracle DDL、索引、视图或改写 SQL |
| 切换前的 SQL Server 库 | 备份后删除；备份作为独立回退资产长期保留 |
| 新 SQL Server 库 | 空库一次 bootstrap；非空库必须精确匹配契约，否则拒绝启动 |
| 数据库删除 | 只存在于 `scripts/cutover/Invoke-EmptyDatabaseCutover.ps1`，且必须停机、备份并人工键入精确目标 |
| PollTrace / projection evidence | 成功和失败轮次按因果契约持久化；执行/结构失败不写入部分投影 |
| Local.json / SharedSecret | 只留在现场，不进发布包、回传包或仓库 |
