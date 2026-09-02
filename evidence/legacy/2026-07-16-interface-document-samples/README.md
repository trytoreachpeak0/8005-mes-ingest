# 旧接口文档中的五类任务样本

本目录由迁移工具从重构前的需求文档表格提取，只用于历史覆盖率基线，不是一次
可追溯的工厂 run，不得由 `import-run` 晋升为 latest。

- 提取行数：642
- 去重 PACKAGE：115
- exact 命中行数：145
- `TOLL-` prefix 命中行数：9
- 未命中行数：488
- 未命中去重 PACKAGE：96（见 `package-coverage/unmatched_packages.csv`）

这些数字只代表文档当时保存的活跃任务样本，不能作为物料 PACKAGE 全集。当前
覆盖率应以新的 `MES_TASK_UNION` 工厂 run 导入后重新分析为准。
