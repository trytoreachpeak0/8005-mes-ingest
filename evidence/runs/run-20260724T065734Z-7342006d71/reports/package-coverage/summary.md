# PACKAGE / 花篮容量规则离线覆盖分析

> 本报告由 `mes.analysis.analyze_package_coverage` 生成。

- 输入：`C:\Users\szy\Desktop\xinji\8005多仓位AGV\mes\evidence\runs\.run-20260724T065734Z-7342006d71.h6x9yw9j\results\MES_TASK_UNION-round-001.csv`
- 总行数：672
- 去重 PACKAGE 数（不含空值）：120
- PACKAGE 空值行数：0
- exact 命中行数：153
- prefix 命中行数：25
- unmatched 行数：494

## 按 TASK_TYPE 汇总

| TASK_TYPE | 总行数 | 空值 | exact | prefix | unmatched |
| --- | ---: | ---: | ---: | ---: | ---: |
| DIE_TO_OVEN | 94 | 0 | 10 | 0 | 84 |
| DIE_TO_WIRE_STAGING | 105 | 0 | 85 | 3 | 17 |
| STAGING_TO_WIRE | 246 | 0 | 11 | 0 | 235 |
| WIRE_TO_GATE | 84 | 0 | 7 | 0 | 77 |
| WIRE_TO_NITROGEN | 91 | 0 | 36 | 20 | 35 |
| WIRE_TO_OPTICAL | 52 | 0 | 4 | 2 | 46 |

## 明细文件

- `unmatched_packages.csv`：未匹配 PACKAGE 聚合。
- `prefix_matches.csv`：显式前缀命中 PACKAGE 聚合。
- `matched_packages.csv`：全部 exact/prefix 命中 PACKAGE 聚合。
