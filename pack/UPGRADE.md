# MesIngest 升级与回滚说明

本说明随 `pack/Publish-MesIngest.ps1` 输出到安装根目录 `UPGRADE.md`。首次安装仍以 `INSTALL.md` 为准。

## 先确认运行面

ADR-mes-0017 Production V2 与历史 Development V1 不是同一套契约：

| 运行面 | 用途 | SQL Server | HTTP |
|---|---|---|---|
| **Production V2 / ADR-mes-0017** | 正式 Oracle 单语句轮询、PollTrace、新投影与冻结契约 | `MesIngest:NewSqlServerConnectionString` 指向专用空库或已精确匹配 V2 schema v17 的库 | 只认 `/api/v2/*` 与 canonical `/openapi/v2.json` |
| **Legacy Development V1** | CSV/内存示例、旧投影和历史 Watch 测试 | `MesIngest:SqlServerConnectionString` 或内存 | 仅 Development 的 `/api/*`、旧 Swagger / `openapi/v1.json` |

生产环境必须配置 `NewSqlServerConnectionString` 和 `SnapshotSource=Oracle`。旧 V1 库不是 V2 就地升级目标：V2 只会在**完全空库**中建立完整 schema，或对既有 V2 库做严格契约校验；遇到旧表、缺列、额外表或版本不匹配时会拒绝启动，不会自动改造或回退到内存。

## 升级前必做

1. **停止当前写入面**

   ```powershell
   Stop-Service MesIngest
   # 如有正在运行的旧 Watch，先全部退出
   ```

2. **备份当前数据库**

   无论当前是 V1 还是 V2，都先做 SQL Server 完整库备份，不要只备份若干表。

   ```sql
   BACKUP DATABASE [MesIngest]
   TO DISK = N'D:\backup\MesIngest_pre_upgrade.bak'
   WITH INIT, CHECKSUM;
   ```

3. **备份当前安装根目录**

   保留完整旧包、`VERSION.txt`、`service/appsettings.Local.json` 以及 Watch 本机配置。已填密钥的 Local.json 不得回传仓库或复制进新发布包。

4. **准备专用 V2 库**

   - 从 V1 切换时：新建一个空数据库（例如 `MesIngestV2`）；保留旧 V1 库用于回滚。
   - 已运行同契约 V2 时：仍先备份原 V2 库，再由新 Host 执行严格契约验证。
   - 不得通过 DROP TABLE、人工补列或复制旧 V1 表来“伪造” V2 schema。

## Production V2 升级步骤

1. 使用新发布包替换安装内容。`service/queries/mes-task-union/query.sql` 是唯一正式 Oracle SQL，必须与相邻 `query.manifest.json` 及根目录 `RELEASE-MANIFEST.json` 一致。删除旧安装根目录遗留的 `queries/`，不得保留第二份 SQL。

2. 从 `templates/appsettings.Local.json.example` 重新创建 `service/appsettings.Local.json`，不要盲目覆盖旧文件。至少核对：

   - `NewSqlServerConnectionString`：指向上一节准备的专用 V2 库；
   - `SnapshotSource=Oracle`；
   - `OracleUser`、`OraclePassword`、`OracleDataSource`；
   - `OracleMode=Thin` 作为默认尝试；
   - 如需 Thick，还必须同时填 `OracleInstantClientDir` 和已注册的 `OracleThickOdbcDriver`；
   - 跨机绑定时的 `Urls` 和 `SharedSecret`。

3. 在安装根目录先执行包校验：

   ```powershell
   .\scripts\Test-ReleasePackage.ps1 -PackageRoot .
   ```

   校验必须确认 canonical SQL 的路径、长度和 SHA-256；同时确认 `openapi/v2.json` 只包含冻结的 `/api/v2/*` GET 面，其契约版本、schema v17 与 SHA-256 写入 `RELEASE-MANIFEST.json`。缺失、空文件、被篡改、出现旧路由/旧术语/写操作或多份 SQL 都必须拒绝。

4. 在 `service/` 目录执行 Thin 真实探针：

   ```powershell
   .\MesIngest.Host.exe --probe-oracle
   ```

   只有 `execution_scope=LIVE_ORACLE`、`connection_attempted=true`、requested/actual mode 一致、canonical query identity 一致、`outcome=Success` 且 `result=PASSED` 才可以进入下一步。离线或 fake/CI 结果只能是 `NOT_EXECUTED`。

   Thin 失败时，只有在已安装 Instant Client 且有注册 Oracle ODBC 驱动时才改为 Thick 重试。Thick 不会静默回退 Thin；错误配置必须修正，不得带病启动服务。

5. 安装/启动服务：

   ```powershell
   .\scripts\install-service.ps1
   Start-Service MesIngest
   ```

   Host 启动时会在空库中一次建立完整 V2 schema；对非空库则只接受完全一致的 schema v17 / contract identity。验证失败时服务不会回退到旧 V1 或内存库。

6. 用 V2 只读证据冒烟：

   - `GET /api/v2/contract`；
   - `GET /openapi/v2.json`，并与包内 `openapi/v2.json` 的 SHA-256、契约版本和 schema v17 严格一致；
   - `GET /api/v2/current-ingest-attention?pageNumber=1&pageSize=100`；
   - 从采集结果取得 PollTraceId，然后 `GET /api/v2/poll-traces/{pollTraceId}`；
   - 核对至少多个不同 PollTrace 的 canonical query version、规范化 content digest、row count 和 outcome；
   - 确认关闭/不启动 Watch 时 PollTrace high-water 仍继续前进。

   首个成功投影完成前，部分运维视图可能暂无快照；等待正式轮询成功，不得改用 V1 poll-health 冒充 V2 证据。建议按 `FACTORY-VALIDATION.md` 和 `validation/Invoke-FactoryValidation.ps1` 采集。

## 冻结契约与切换声明

- `openapi/v2.json` 是唯一 Production V2 canonical 文档；包内文件 SHA-256 必须匹配发布清单，运行时 `/openapi/v2.json` 必须与它完整 JSON 语义严格一致。`openapi/v1.json` 只属于 Development 旧面，不能作为 V2 契约证据。
- 当前打包的 Watch 与其历史冒烟流程仍属于 V1。Ticket 24 负责把发布冒烟和 Watch 迁移到冻结后的 V2 契约。
- Production 必须让旧 `/api/contract`、`/api/demands`、`/api/alerts`、`/api/poll-health`、`/api/demand-changes` 和 `/openapi/v1.json` 全部返回 404；即使配置请求开启开发旧面也不例外。任一旧面可达时，不得宣称 ADR-mes-0017 Production 切换完成。

## 失败时回滚

1. 停止 MesIngest Service，退出所有 Watch。
2. 恢复升级前备份的安装目录和原 `service/appsettings.Local.json`。
3. 恢复对应旧版二进制的 SQL Server 库：

   ```sql
   ALTER DATABASE [MesIngest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [MesIngest]
   FROM DISK = N'D:\backup\MesIngest_pre_upgrade.bak'
   WITH REPLACE;
   ALTER DATABASE [MesIngest] SET MULTI_USER;
   ```

   如果此次是从 V1 切换到新建 `MesIngestV2` 库，旧 V1 库本就应保持不变；将旧服务配置重新指向它即可。不要为了回滚破坏或复用新 V2 库。

4. 启动旧版 Host，只按它自身的契约做冒烟。
5. 保留失败的脱敏探针、Host 日志和 V2 PollTrace 证据供调查；不得回传 SQL、连接串、凭据、datasource 或原始 MES 值。

## 数据与只读承诺

| 对象 | Ticket 15 行为 |
|---|---|
| 客户 Oracle / 批准 `MES_TASK_UNION` SQL | 仅执行一条已校验的只读查询；不部署 Oracle DDL、索引、视图或改写 SQL |
| 旧 V1 SQL Server 库 | 作为独立回滚资产保留；不就地升级到 V2 |
| V2 SQL Server 库 | 空库可一次 bootstrap；非空库必须精确匹配 schema v17，否则拒绝启动 |
| PollTrace / projection evidence | 成功和失败轮次按 V2 因果契约持久化；执行/结构失败不写入部分投影 |
| Local.json / SharedSecret | 只留在现场，不进发布包、回传包或仓库 |
