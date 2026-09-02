# MES字段类型与长度说明

## 1. 数据来源

- 查询结果文件：`查询MES字段类型与长度result.csv`
- Oracle当前用户：`FWMES`
- Oracle当前Schema：`FWMES`
- 会话时区：`+08:00`
- 数据库时区：`+08:00`
- 结论：MES查询时间按北京时间 `UTC+8` 解释，与当前Oracle会话及数据库时区一致。

## 2. MES对象字段定义

### 2.1 V_FW_WIP_SUBLOT

| 字段 | Oracle类型 | 最大长度 | 可空 | 用途 |
| --- | --- | ---: | --- | --- |
| `LOT` | `VARCHAR2(40 BYTE)` | 40字节 | 否 | 接口输出的SUBLOT |
| `WORKORDER` | `VARCHAR2(45 BYTE)` | 45字节 | 否 | SQL内部字段 |
| `STEP` | `VARCHAR2(255 BYTE)` | 255字节 | 是 | 接口STEP及SQL筛选 |
| `STATE` | `VARCHAR2(20 BYTE)` | 20字节 | 否 | SQL筛选 |
| `TASK` | `VARCHAR2(20 BYTE)` | 20字节 | 否 | SQL筛选 |
| `EQP` | `VARCHAR2(50 BYTE)` | 50字节 | 是 | 第5类任务目标机台及SQL关联 |
| `LASTTIME` | `DATE` | 7字节内部存储 | 否 | 第5类任务DATES |

### 2.2 FW_WIP_TRANS

| 字段 | Oracle类型 | 最大长度 | 可空 | 默认值 | 用途 |
| --- | --- | ---: | --- | --- | --- |
| `LOT` | `VARCHAR2(50 BYTE)` | 50字节 | 是 |  | SQL关联 |
| `DATES` | `DATE` | 7字节内部存储 | 是 |  | 第1～4类任务相关时间 |
| `TASK` | `VARCHAR2(50 BYTE)` | 50字节 | 否 |  | SQL筛选 |
| `EQP` | `VARCHAR2(50 BYTE)` | 50字节 | 是 | `'N/A'` | 第1～4类任务机台 |

### 2.3 FW_EQPRES_EQPINFORMATION

| 字段 | Oracle类型 | 最大长度 | 可空 | 用途 |
| --- | --- | ---: | --- | --- |
| `EQPNO` | `VARCHAR2(30 BYTE)` | 30字节 | 否 | 机台资料关联键 |
| `AREA` | `VARCHAR2(30 BYTE)` | 30字节 | 是 | MES机台区域号 |
| `STEP` | `VARCHAR2(20 BYTE)` | 20字节 | 否 | 机台资料筛选 |

## 3. 合并任务查询输出契约

| 输出字段 | 建议应用类型 | 来源及长度依据 | 是否允许为空 |
| --- | --- | --- | --- |
| `TASK_TYPE` | 字符串/枚举 | 应用定义的固定任务类型代码 | 否 |
| `SUBLOT` | 字符串，至少容纳40字节 | `V_FW_WIP_SUBLOT.LOT` | 业务不允许为空 |
| `AREA` | 可空字符串，至少容纳30字节 | `FW_EQPRES_EQPINFORMATION.AREA` | 是 |
| `EQP` | 字符串，至少容纳50字节 | `V_FW_WIP_SUBLOT.EQP`或`FW_WIP_TRANS.EQP` | 数据库定义可空，业务不允许为空 |
| `STEP` | 可空字符串，至少容纳255字节 | `V_FW_WIP_SUBLOT.STEP` | 是；系统不依赖其解释任务类型 |
| `DATES` | 日期时间 | Oracle `DATE`，按北京时间UTC+8解释 | 数据库部分来源定义可空，业务约定不为空 |

## 4. 实现注意事项

1. Oracle字段采用 `BYTE` 长度语义。中文字符可能占多个字节，应用层不要把“40字节”误认为“必然可存40个中文字符”。
2. Oracle `DATE`不携带时区信息，但当前数据库和会话时区均为 `+08:00`，系统按北京时间解释。
3. 数据库字典显示部分EQP和DATES字段允许为空，这与“查询结果中业务保证不为空”并不冲突。应用仍须校验，实际返回空值时作为阻断性异常处理。
4. 合并SQL的EQP输出应按最大来源长度50字节设计，不能只按设备资料表EQPNO的30字节设计。
5. STEP最大声明长度为255字节，即使当前样本值较短，应用数据库和接口模型也不应只按样本长度设计。
6. 本次CSV包含字段声明类型和最大长度，但未包含“当前已有数据的实际最大字符数/字节数”查询结果。系统字段设计应以声明最大长度为准，不依赖当前样本最大值。
