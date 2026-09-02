# MesIngestWatch 保持业务只读，并允许受限遥测追加

MesIngestWatch 需要把客户端解码、呈现和投影可见滞后纳入 Host/SQL Server 的权威结构化可观测数据，但让 Watch 直写 SQL 或只留本机日志都会破坏薄客户端或中央分析边界。决定保持 MES、TransportDemand、IngestAlert、PollHealth 与其它业务投影完全只读，仅允许 Watch 通过受访问控制、字段白名单、追加式且幂等的专用入口上报 WatchRefreshTrace；该入口不得承载业务命令、任意日志或敏感内容。

Watch 的 AreaFilterProfile 属于当前 Windows 用户的本地显示配置，不写入 Host 或业务投影。为保证 DemandSeries 与资格审计在 AREA 筛选后的总数和分页正确，Watch 可以把配置中的 MesArea 集合作为临时只读查询条件发送给 Host，由 Host 在分页前筛选；该条件不改变外部可读资格、ExternallyReadableDemandCatalog、CatalogRevision 或 Dispatch 范围。Host 与 Watch 必须使用相同 API 契约版本，不为旧版组合提供客户端单页过滤降级。
