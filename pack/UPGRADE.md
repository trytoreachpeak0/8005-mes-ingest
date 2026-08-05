# MesIngest 升级与回滚说明

将增量投影、Alert incident、DemandChangeFeed 与新 Watch 配置作为**可重复**的现有安装升级交付。目标：不丢失永久 GONE / DemandId 历史，失败后可恢复到升级前状态。

本说明随 `pack/Publish-MesIngest.ps1` 输出到安装根目录 `UPGRADE.md`。首次安装仍以 `INSTALL.md` 为准。

## 升级前强制备份（前置条件）

1. **停止写入面**
   ```powershell
   Stop-Service MesIngest
   # 如有正在运行的 Watch，先全部退出
   ```
2. **备份 SQL Server 投影库**（完整库备份，不是只导几张表）
   ```sql
   BACKUP DATABASE [MesIngest]
   TO DISK = N'D:\backup\MesIngest_pre_upgrade.bak'
   WITH INIT, CHECKSUM;
   ```
3. **备份运行目录**
   - 整包复制当前安装根（含 `service/appsettings.Local.json`、`watch/` 本地配置）
   - 勿把填好的 `appsettings.Local.json` 回传到仓库或新发布包
4. **记录版本**
   - 保存当前 `VERSION.txt`
   - 可选：`GET /api/contract`（升级后会返回 `contractVersion` / `schemaVersion`）

未完成备份不得覆盖 `service/` / `watch/`。

## 升级步骤

1. 用新发布包覆盖安装目录中的 `service/`、`watch/`、`queries/`、`openapi/`、文档与脚本（保留现场 `service/appsettings.Local.json` 与 Watch 本机配置）。
2. 对照 `templates/appsettings.Local.json.example` 与 `templates/watch.appsettings.Local.json.example`，确认新增键已写入现场配置：
   - Host：`ChangeFeedRetentionHours`（默认 48）、`AlertRetentionDays`（默认 365）
   - Watch：`RequestTimeoutSeconds`（默认 30）、`ConnectionLogRetentionDays` / `ConnectionLogMaxSizeMb`、`RenderingMode`（默认 `SoftwareOnly`）
   - 分页 `limit` 硬上限 1–200（默认 100）写在 OpenAPI / 代码中，不是 appsettings 键
3. 启动 Host：
   ```powershell
   Start-Service MesIngest
   ```
4. Host 启动时 `EnsureSchema` **幂等、仅增量、单事务**：
   - 不为 TransportDemands 做 DROP/重建
   - 修改 Phase-1 `IngestAlerts` 前先写入 `IngestAlerts_LegacyArchive`，再补 incident 列并就地迁移
   - 创建 ChangeFeed / 索引 / `MesIngestSchemaVersion`；全部 DDL 在同一显式事务中提交，中途失败回滚到升级前一致点（无半迁移），修复条件后可再次启动完成升级
5. 冒烟：
   - `GET /api/contract` 的 `contractVersion` 与同包 Watch 一致
   - `GET /api/poll-health`、`GET /api/demands`、`GET /api/alerts`
   - 启动同包 `watch\MesIngest.Watch.exe`；版本不匹配时横幅显示 `CONTRACT_VERSION_MISMATCH`，**不会静默空板**
6. 离线契约：安装根 `openapi/v1.json`（与运行中 `/openapi/v1.json` 同契约）

## 失败时恢复（回滚）

1. 停止服务与 Watch。
2. 用升级前备份还原安装目录（至少 `service/`、`watch/`）。
3. 还原 SQL Server：
   ```sql
   ALTER DATABASE [MesIngest] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [MesIngest]
   FROM DISK = N'D:\backup\MesIngest_pre_upgrade.bak'
   WITH REPLACE;
   ALTER DATABASE [MesIngest] SET MULTI_USER;
   ```
4. 启动旧版 Host，确认 DemandId / VISIBLE / GONE / pause / alerts / poll-health 仍在。
5. 调查失败原因后再重试升级；**禁止**用 DROP TABLE / 重建库“清掉半迁移”。

## 数据保留承诺

| 对象 | 升级行为 |
|------|----------|
| TransportDemand（含永久 GONE） | 保留；禁止 DROP/全表重建 |
| TaskTypePauses | 保留 |
| Phase-1 IngestAlerts 消息行 | 先归档到 `IngestAlerts_LegacyArchive`，再就地补 incident 列；迁移后标记为 **inactive / ResolvedAt=CreatedAt**（历史保留可查，不作为当前活动横幅） |
| PollHealth | 保留；按需补列 |
| DemandChangeFeed | 新建或幂等补齐；保留期默认 48h |
| 客户 Oracle / 批准 MES SQL | **只读**；本升级不部署任何 Oracle DDL |

DDL 在单事务中执行且各步幂等：中途失败整批回滚，不留下半套列/半套对象；修复条件后再启 Host 可安全重入完成升级，**不会** DROP/重建 `TransportDemands`。业务级回滚（回到旧版二进制）仍依赖升级前库备份（见上）。

## 版本不匹配

Watch 与 Host 必须来自**同一安装包**。若 `GET /api/contract` 缺失或 `contractVersion` 不一致，Watch 显示明确的 `CONTRACT_VERSION_MISMATCH` 并拒绝用错契约刷空板。
