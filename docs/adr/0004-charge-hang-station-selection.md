# 充电改派选桩：允许集合内 NearStationQuery

ChargeHangReassign 选备用桩时：使用配置的允许充电站集合，排除刚失败的站点后，在集合内用 NearStationQuery 选路径代价最近的充电站；不以单纯顺序列表或失败站→替代站硬映射为默认。

**Status**: accepted

**Considered Options**: 配置顺序列表；仅 Near（全图候选）；允许集合 + Near（采纳）；失败站硬映射。
