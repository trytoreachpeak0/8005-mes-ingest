/*
用途：按 SUBLOT 查询该批次在各工序历史上出现过的最大料盒数。

说明：
1. 非运输任务查询，不参与六类任务 UNION ALL 轮询。
2. 入参 :sublot 为现场扫描的子批次号，对应 MES 表字段 fw_wip_box_his.lot。
3. 输出列 max_box_count 表示各 STEP 分组统计的料盒数中的最大值。
4. 本 SQL 只允许以只读方式执行；应用层使用绑定变量传入 SUBLOT，禁止拼接 SQL。
*/

SELECT MAX(step_box_count) AS max_box_count
FROM   (
        SELECT COUNT(t.box) AS step_box_count,
               t.step
        FROM   fw_wip_box_his t
        WHERE  t.lot = :sublot
        GROUP  BY t.step
       )
