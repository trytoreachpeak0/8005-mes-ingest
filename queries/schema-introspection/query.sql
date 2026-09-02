/*
用途：以单条只读查询核验 MES 任务接口涉及字段的 Oracle 声明。

说明：
1. 返回当前用户、Schema、会话/数据库时区以及每个必需字段的字典定义。
2. 即使字段不存在也保留一行，并用 FOUND='N' 标识。
3. 不扫描业务表计算实际最大长度；该高负载研究必须另行获得客户 IT 批准。
*/
WITH required_columns AS (
    SELECT 1 AS object_order, 'V_FW_WIP_SUBLOT' AS object_name, 1 AS column_order, 'LOT' AS column_name FROM dual
    UNION ALL SELECT 1, 'V_FW_WIP_SUBLOT', 2, 'WORKORDER' FROM dual
    UNION ALL SELECT 1, 'V_FW_WIP_SUBLOT', 3, 'STEP' FROM dual
    UNION ALL SELECT 1, 'V_FW_WIP_SUBLOT', 4, 'STATE' FROM dual
    UNION ALL SELECT 1, 'V_FW_WIP_SUBLOT', 5, 'TASK' FROM dual
    UNION ALL SELECT 1, 'V_FW_WIP_SUBLOT', 6, 'EQP' FROM dual
    UNION ALL SELECT 1, 'V_FW_WIP_SUBLOT', 7, 'LASTTIME' FROM dual
    UNION ALL SELECT 2, 'FW_WIP_TRANS', 1, 'LOT' FROM dual
    UNION ALL SELECT 2, 'FW_WIP_TRANS', 2, 'DATES' FROM dual
    UNION ALL SELECT 2, 'FW_WIP_TRANS', 3, 'TASK' FROM dual
    UNION ALL SELECT 2, 'FW_WIP_TRANS', 4, 'EQP' FROM dual
    UNION ALL SELECT 3, 'FW_EQPRES_EQPINFORMATION', 1, 'EQPNO' FROM dual
    UNION ALL SELECT 3, 'FW_EQPRES_EQPINFORMATION', 2, 'AREA' FROM dual
    UNION ALL SELECT 3, 'FW_EQPRES_EQPINFORMATION', 3, 'STEP' FROM dual
)
SELECT USER AS current_user,
       SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') AS current_schema,
       SESSIONTIMEZONE AS session_timezone,
       DBTIMEZONE AS database_timezone,
       r.object_name,
       r.column_name,
       CASE WHEN c.column_name IS NULL THEN 'N' ELSE 'Y' END AS found,
       c.data_type,
       CASE
           WHEN c.data_type IN ('CHAR', 'VARCHAR2', 'NCHAR', 'NVARCHAR2') THEN
               c.data_type || '(' || c.char_length ||
               CASE c.char_used WHEN 'C' THEN ' CHAR' WHEN 'B' THEN ' BYTE' END || ')'
           WHEN c.data_type = 'NUMBER' AND c.data_precision IS NOT NULL THEN
               'NUMBER(' || c.data_precision ||
               CASE WHEN c.data_scale IS NOT NULL THEN ',' || c.data_scale END || ')'
           WHEN c.data_type LIKE 'TIMESTAMP%' AND c.data_scale IS NOT NULL THEN
               c.data_type || '(' || c.data_scale || ')'
           ELSE c.data_type
       END AS full_data_type,
       c.data_length AS declared_max_bytes,
       c.char_length AS declared_max_chars,
       c.char_used,
       c.data_precision,
       c.data_scale,
       c.nullable,
       c.data_default
FROM required_columns r
LEFT JOIN all_tab_columns c
  ON c.owner = UPPER(SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))
 AND c.table_name = r.object_name
 AND c.column_name = r.column_name
ORDER BY r.object_order, r.column_order
