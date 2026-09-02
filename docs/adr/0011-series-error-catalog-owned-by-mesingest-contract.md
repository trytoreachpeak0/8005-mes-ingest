# Series 错误目录由 MesIngest 领域契约拥有

错误检索必须让历史错误在不同 Host、Watch 和目录版本下保持一致解释。决定由 MesIngest 领域代码拥有版本化 SeriesErrorCatalog：错误码发布后不换义或复用，每个错误码只有一个稳定主分类，Watch 只消费 Host/API 发布的目录；不采用 Watch 本地白名单或可运行时编辑的数据库分类，以免分页计数与历史含义随客户端或配置漂移。

**Status**: accepted
