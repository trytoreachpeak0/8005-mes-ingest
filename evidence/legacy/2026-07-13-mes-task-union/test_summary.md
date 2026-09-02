# MES五类任务合并查询测试汇总

- 生成时间：2026-07-13 16:25:05
- Python：3.14.6

## 阶段1：单次合并查询

- 开始：2026-07-13 16:22:57
- 完成：2026-07-13 16:23:00
- 耗时：3.28s
- 行数：641
- 错误：无
- 性能目标(≤5s)：通过

### 任务类型数量

- `DIE_TO_WIRE_STAGING`：108
- `DIE_TO_OVEN`：110
- `WIRE_TO_GATE`：113
- `WIRE_TO_OPTICAL`：77
- `STAGING_TO_WIRE`：233

### 质量检查

- 未知TASK_TYPE：无
- EQP空值：0
- DATES空值：0
- AREA空值：0
- 完全重复组：0
- TASK_TYPE+SUBLOT冲突：0
- 跨任务类型冲突：0
- 关卡/三光互斥冲突：0
- 同一EQP多AREA：0
- DATES最早：2026-04-26 18:50:45
- DATES最晚：2026-07-13 16:22:31
- 早于上线时间2026-08-01：641
- 超过声明长度字段：无
- 可疑短AREA：['N']

## 阶段2：独立SQL对比

- `DIE_TO_WIRE_STAGING`：original=108，merged=108，diff=0，耗时=1.24s，success=Y
- `DIE_TO_OVEN`：original=110，merged=110，diff=0，耗时=0.39s，success=Y
- `WIRE_TO_GATE`：original=113，merged=113，diff=0，耗时=0.56s，success=Y
- `WIRE_TO_OPTICAL`：original=77，merged=77，diff=0，耗时=0.30s，success=Y
- `STAGING_TO_WIRE`：original=233，merged=233，diff=0，耗时=0.93s，success=Y

## 阶段3：连续性能测试

- 轮次：10
- 成功轮次：10
- 平均耗时：3.13s
- 最大耗时：4.37s
- 全部成功：是
- 平均≤5s：是
- 最大≤10s：是
