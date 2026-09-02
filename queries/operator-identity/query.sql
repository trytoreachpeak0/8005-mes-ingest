/*
用途：按工号查询 MES 操作员姓名，用于现场身份核验与操作记录关联。

说明：
1. 非运输任务查询，不参与六类任务 UNION ALL 轮询。
2. 入参 :user_id 为现场扫描工牌一维码或手动输入的工号，对应 MES 视图字段 mv_fw_username.usercode。
3. 输出列 operator_name 为操作员姓名，对应 MES 字段 mv_fw_username.username。
4. 本 SQL 只允许以只读方式执行；应用层使用绑定变量传入工号，禁止拼接 SQL。
5. 查询无结果时表示 MES 中不存在该工号，应用层应拒绝建立操作会话（见 UC-043 / FR-011）。
*/

SELECT username AS operator_name
FROM   mv_fw_username
WHERE  usercode = :user_id;
