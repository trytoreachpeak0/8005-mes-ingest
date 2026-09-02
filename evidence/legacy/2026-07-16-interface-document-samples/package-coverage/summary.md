# PACKAGE / 花篮容量规则离线覆盖分析

> 本报告由 `mes.analysis.analyze_package_coverage` 生成。

- 输入：`mes\evidence\legacy\2026-07-16-interface-document-samples\legacy-task-samples.csv`
- 总行数：642
- 去重 PACKAGE 数（不含空值）：115
- PACKAGE 空值行数：0
- exact 命中行数：145
- prefix 命中行数：9
- unmatched 行数：488

## 按 TASK_TYPE 汇总

| TASK_TYPE | 总行数 | 空值 | exact | prefix | unmatched |
| --- | ---: | ---: | ---: | ---: | ---: |
| DIE_TO_OVEN | 125 | 0 | 8 | 0 | 117 |
| DIE_TO_WIRE_STAGING | 112 | 0 | 99 | 6 | 7 |
| STAGING_TO_WIRE | 221 | 0 | 11 | 1 | 209 |
| WIRE_TO_GATE | 110 | 0 | 11 | 2 | 97 |
| WIRE_TO_OPTICAL | 74 | 0 | 16 | 0 | 58 |

## 明细文件

- `unmatched_packages.csv`：未匹配 PACKAGE 聚合。
- `prefix_matches.csv`：显式前缀命中 PACKAGE 聚合。
- `matched_packages.csv`：全部 exact/prefix 命中 PACKAGE 聚合。
