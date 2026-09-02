# OrderHang：充电与普通分支分开；普通按原因自动尝试 HangContinue

充电失败 OrderHang：人工排查，或改派其它充电桩再充；不以 HangContinue 为默认恢复。普通（非充电）OrderHang：按原因分支——关机恢复、单机取消等可恢复类自动尝试 HangContinue，并复核订单是否真正离开 HANG；解抱闸等须先确认车态就绪再试，未就绪仅报警；HangContinue 失败（业务拒绝或仍为 HANG）转人工，不自动 CANCEL。急停不建模为单独进 HANG。依据 Lab BC-ORDER-015/018。

**Status**: accepted

**Considered Options**: 一律人工；见 HANG 即自动 HangContinue；按原因分支自动 HangContinue（采纳）；失败则自动 CANCEL 重派；充电可人工或改桩 + 普通等 Lab（已被本决策取代 pending 部分）。
