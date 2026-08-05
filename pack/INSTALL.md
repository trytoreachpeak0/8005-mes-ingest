# MesIngest 工厂安装说明

自包含安装目录（由 `pack/Publish-MesIngest.ps1` 生成）可整包拷到工厂机。默认只读 HTTP 仅监听本机；开放非本机访问时必须配置共享密钥。

## 目录结构

```
MesIngest/
  service/                 # Windows Service（MesIngest.Host）自包含发布
  watch/                   # 可选 WPF 盯盘客户端（MesIngest.Watch）
  queries/                 # 正式 MES_TASK_UNION SQL（与仓库原稿一致）
  templates/               # 填空配置模板（无真实凭证）
  scripts/                 # 安装 / 卸载辅助脚本
  validation/              # 工厂验证回传模板
  openapi/v1.json          # 静态 OpenAPI 契约（离线导入 Postman/代码工具）
  INSTALL.md               # 本说明
  UPGRADE.md               # 现有安装升级、备份前置与回滚
  FACTORY-VALIDATION.md    # 工厂执行与核验清单
  VERSION.txt              # 发布版本信息
```

## 配置（凭证不进包）

1. 复制 `templates/appsettings.Local.json.example` → `service/appsettings.Local.json`
2. 可选：复制 `templates/watch.appsettings.Local.json.example` → `watch/appsettings.Local.json`（或直接改 `watch/appsettings.json`）
3. 填写 SQL Server、Oracle 等占位符；**不要**把填好的文件拷回仓库或再打进安装包
4. 默认 `Urls` 为 `http://127.0.0.1:5088`（仅本机）
5. 若改为非本机绑定（如 `http://0.0.0.0:5088` 或局域网 IP），必须同时设置 `SharedSecret`；调用方携带：

   `Authorization: Bearer <SharedSecret>`

6. WPF 非本机访问时，在 Watch 配置（或环境变量 `MesIngestWatch__SharedSecret`）填写同一密钥

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

## 可选 WPF

Service 运行后启动 `watch\MesIngest.Watch.exe`。关闭 WPF **不会**停止 Service。默认连接 `http://127.0.0.1:5088`。

## 日志位置

- **Windows Service**：写入 Windows **应用程序**事件日志（来源通常为 `.NET Runtime` / 进程名）；用「事件查看器」筛选 MesIngest.Host
- **控制台运行**：日志输出到当前终端（stdout/stderr）
- **Watch 连接事件**：本机 `%LocalAppData%\MesIngest.Watch\logs\` 按日 JSONL（首次失败 / 每 5 分钟摘要 / 恢复）；默认保留 30 天或 100 MB（先到先清理）。可用 `Watch:ConnectionLogRetentionDays` / `Watch:ConnectionLogMaxSizeMb`（或 `MesIngestWatch__*`）调整；`MesIngestWatch__LogDirectory` 可改写日志目录
- **Watch 渲染**：默认 `Watch:RenderingMode=SoftwareOnly`，规避虚拟/远程显示驱动导致的空白客户区；仅在确认硬件渲染兼容时改为 `Auto`
- ASP.NET 默认级别见 `service/appsettings.json` 的 `Logging` 节；可按现场需要调高

## Watch HTTP 超时

默认 `Watch:RequestTimeoutSeconds=30`（合法范围 1–300）。环境变量 `MesIngestWatch__RequestTimeoutSeconds` 可覆盖。非法值会使 Watch 启动失败并指出配置键与值。断线时保留最后一次成功数据，不在同一次刷新内立即重试。

## 版本信息

见安装根目录 `VERSION.txt`（发布脚本写入时间与目标 RID）。程序集版本也可在 `service\MesIngest.Host.exe` 文件属性中查看。

## 基本故障排查

| 现象 | 检查 |
|------|------|
| Service 无法启动 | `service/appsettings.Local.json` 是否存在；`Urls` 非本机时是否设置了 `SharedSecret`；事件查看器中的异常 |
| API 401 | 非本机绑定时是否带了 `Authorization: Bearer …`，密钥是否与配置一致 |
| 空板 / 无需求 | `GET /api/poll-health`、`GET /api/alerts`；是否 `POLL_FAILURE` / `PAUSED_ZERO_DROP`；Oracle 探针是否成功 |
| Oracle 连不上 | `OracleMode` Thin→Thick + Instant Client 路径；账号/数据源；工厂网络 |
| SQL Server 投影丢失 | `SqlServerConnectionString`；库是否可连；空连接串会退回内存（重启丢数据） |
| 查询 SQL 缺失 | 确认 `service/queries/mes-task-union/query.sql`（或根 `queries/`）存在 |

只读 API（本机默认）：

- `GET /api/contract`
- `GET /api/demands`
- `GET /api/demands/{demandId}`
- `GET /api/alerts`
- `GET /api/poll-health`
- `GET /api/demand-changes`

人工试调与合作者文档：

- 浏览器打开 `http://127.0.0.1:5088/swagger`（远程绑定时文档仍默认启用）
- 机器可读契约：`GET /openapi/v1.json`（安装包离线副本：`openapi/v1.json`）
- 文档元数据可匿名打开；实际 `/api/*` 在非本机绑定时仍需 `Authorization: Bearer <SharedSecret>`（Swagger UI 点 Authorize 后再 Try it out）

工厂连通验证、人工核验与回传约定见同包 [`FACTORY-VALIDATION.md`](FACTORY-VALIDATION.md) 与 `validation/`；本说明只覆盖安装与安全配置。
