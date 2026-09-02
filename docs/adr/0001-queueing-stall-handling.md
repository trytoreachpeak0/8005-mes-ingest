# QueueingStall：预防 + 下单前清积压；超时只报警

MES 对 QUEUEING 滞留采用：建单前做 RouteCost/车态等预防自检；向某车下新单前，按 BC-ORDER-013 清掉该车相关 QUEUEING/HELD 积压并确认 IDLE 后再派；若检测到 Stall 超时则报警并转人工，不自动 CANCEL。不用 PriorityExec 作为 Stall 的默认解脱手段（插队是可选能力，不是 Stall 策略）。

**Status**: accepted

**Considered Options**: 仅预防+人工；超时自动取消；仅下单前清积压；Stall 时默认插队；预防+清积压+超时报警不自动取消（采纳）。
