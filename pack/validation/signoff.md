# MesIngest 工厂验证 — 签字页

本页仅记录**人工验收**。勾选或签名**不**等于仓库侧「验证包已就绪」；未签字也**不阻塞**本地后续开发。

## 声明

- 验证包已就绪：由仓库交付安装目录中的 `FACTORY-VALIDATION.md` 与 `validation/` 模板定义；与本页无关。
- 工厂已签字通过：仅当下方执行人确认现场步骤与核验完成后填写。

## 现场确认

- run_id：
- 安装包版本（`VERSION.txt`）：
- 有效 Oracle 模式：Thin / Thick
- 多轮轮询已观察：是 / 否
- A/B/C 链路分阶段延迟证据已采集：是 / 否
- DATES/STEP 逐 TASK_TYPE 语义已与 MES 页面/客户 IT 对照：是 / 否
- UTC+08:00 来源解释与 Watch 本机时区显示已核验：是 / 否
- Swagger + SharedSecret 只读 GET 已核验：是 / 否
- VISIBLE 投影与告警已人工核验：是 / 否
- WPF 展示与横幅已核验：是 / 否
- 关闭 WPF 后 Service/HTTP 仍工作：是 / 否

## 签字

- 执行人姓名：
- 日期：
- 结论：通过 / 有条件通过 / 不通过
- 条件或否决原因：
- 客户方备注：
