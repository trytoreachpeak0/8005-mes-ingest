# 充电 OrderHang 由 MES 自动改派其它充电桩

充电失败进入 OrderHang 后，MES 自动选择备用充电桩并重派充电任务；不以 HangContinue 作为充电失败的默认恢复。改派前**必须先 CANCEL** 旧 HANG 单并确认终态，再下新充电单。如何选桩与重试上限另条决策冻结。

**Status**: accepted

**Considered Options**: 纯人工；MES 人工点「改充」；MES 自动改充且先 CANCEL 再派（采纳）；先试新单再 CANCEL；不 CANCEL 只靠新单顶掉。
