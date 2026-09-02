# Series 错误历史与当前接入关注项分离

错误检索需要永久解释 DemandSeries 错误的发生、恢复和复发，而接入告警需要集中展示此刻值得关注的 Series 及全局轮询或 WorkType 异常。决定允许两页对活动 Series 错误有意重叠，但接入告警只引用稳定错误身份、不另建 fingerprint incident 生命周期，已恢复历史只在错误检索中出现；这样保留当前运维入口，同时避免两套生命周期产生冲突事实。

**Status**: accepted
