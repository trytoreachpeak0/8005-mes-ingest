# 充电自动改充重试上限 N=2

ChargeHangReassign 自动改充最多成功发起 2 次改派（与允许集合大小取较小值）；每次仍遵循先 CANCEL 旧 HANG 再选桩派单。用尽后报警转人工，不再自动改充。禁止无限重试。

**Status**: accepted

**Considered Options**: 只 1 次；扫完允许集合；最多 N 次且 N=2（采纳）；无限重试。
