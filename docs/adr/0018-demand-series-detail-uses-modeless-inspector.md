# DemandSeries 详情使用单实例非模态 Inspector

DemandSeries 主列表与完整详情在固定页面内无法同时获得足够画布。决定从主页面移除内联详情、splitter 和折叠开关，让列表占满剩余页面；用户首次显式打开一个单实例、非模态 Inspector，窗口存在期间自动跟随列表选择并复用同一冻结快照状态，不独立刷新。关闭 Inspector 即结束该窗口；离开 DemandSeries 页面时仍保留最后成功详情及其快照时间。第一版不提供固定 Series，以单屏窗口切换和双屏并排能力换取新增的多窗口生命周期与自动化验证成本。

Inspector 是显示在任务栏和 Alt+Tab 中的普通顶层窗口，不设置 WPF Owner 或 Topmost。首次打开和用户再次执行显示命令可以激活它；列表选择变化只更新内容，不能恢复、激活或抢焦点。Inspector 关闭时列表选择不请求详情，重新打开后才读取当前选择；主窗口关闭必须主动关闭 Inspector 并退出应用。窗口偏好保存正常宽高、位置、所在显示器及最大化状态，恢复时将失效坐标约束回当前可见工作区。

切换选择时 Inspector 立即标识目标 Series 并清除上一对象正文，只有目标 Series、冻结快照引用和选择代次全部匹配时才原子提交新详情；切换请求失败显示目标对象失败态。当前对象的自动刷新失败则保留上一成功详情并明确标记陈旧，成功刷新后对象离开当前结果则清空详情而不跨范围寻找。离开 DemandSeries 页面时显示刷新暂停和最后成功快照，返回后才恢复页面刷新；关闭 Inspector 会取消仍在进行的详情请求。

主页面在页面标题命令区提供可见的“打开／显示详情窗口”命令，双击和 Enter 是等价加速器，Space 只改变选择；主列表不增加详情状态列。Inspector 使用不含主导航的 FluentWindow，采用 E 信息架构：顶部只有紧凑的 Series 与冻结快照上下文，主体以“世代分析”和“事件”两个一级 Tab 分开调查任务；垂直、可滚动的世代列表是唯一世代导航，所选世代与当前世代分别表达。窗口默认 1200×800 epx、最小 720×600 epx，并在窄于现有响应断点时纵向重排而不裁切。Alt+F4 正常关闭 Inspector，Escape 不承担关闭窗口语义。

普通 DemandSeries 导航只打开列表；从概览、资格审计、错误检索或当前关注项发出的精确 Series 下钻会定位目标并创建或显示 Inspector，来源快照比较也在 Inspector 中解释。携带的 FocusedDemandId 建立 Demand 世代初始选择并在同 Series 刷新中尽量保留，手动切换 Series 时清除。同一 Series 刷新保留仍有效的世代、错误期间、证据模式、列宽和滚动位置；切换 Series 时恢复默认详情状态。关闭并重新创建 Inspector 时只恢复窗口几何，不恢复已经结束的详情内部调查上下文。

Inspector 不持有 API Client、Timer 或独立快照，只接收主窗口在现有渲染和状态转换点推送的不可变 DemandSeries presentation。该 presentation 只从冻结的生产 `DemandSeriesDetailSnapshot` 投影：每代形成原因来自匹配 DemandId 的真实 `TRANSPORT_DEMAND_CREATED` 事件，已知码以中文为主、原始码为次要技术信息，未知或畸形原因使用中性回退；形成事实按真实事件、PollTrace 和 ProjectionCommit 构成可变有序集合，不用固定箭头叙事推断生命周期。事件页只保留真实不可变事件并按 `SeriesSequence` 排序，本地 DemandId 过滤不发起查询、不改变事实顺序。

MES 边界按实际 PollTrace、ProjectionCommit 和 assignment 分组，重复原始行按 ordinal 全部保留。只有每个适用侧恰有一条 assigned 原始行时，presentation 才提供 TASK_TYPE、SUBLOT、AREA、EQP、STEP、DATES（MesSourceDate）和 PACKAGE 的标量证据；零行明确为缺失，多行明确为冲突，均不得挑选、合并或覆盖成单值。MES 字段变化只解释边界观察差异，不作为 DemandId 形成原因。生产状态词汇保持权威，包括 `LONG_GONE_BUT_VISIBLE`，不得引入原型专用状态或便利事件。

现有详情抽为不访问 Session 的 DemandSeriesDetailView，由单实例 Coordinator 管理 Inspector 窗口。Session 将“设置稳定选择”和“加载当前选择详情”表达为两个明确操作，使 Inspector 关闭时不读取不可见详情。Inspector 几何以向后兼容字段存入当前用户的 WatchV2Preferences；应用显式指定主窗口退出语义，并由 Composition/Coordinator 统一取消请求和释放多窗口资源。

同一 Host 暂时断线时 Inspector 保留上一成功详情并显示连接与快照陈旧状态，只有 DemandSeries 页面重新激活后才恢复刷新；应用新 Host 设置会关闭 Inspector 并清除旧 Host 详情，但保留窗口几何。主窗口与 Inspector 独立最小化，Inspector 跟随应用系统主题且不提供独立主题或完整导航壳。现有“记住窗口尺寸”升级为向后兼容的“记住窗口布局”，统一控制主窗口布局与 Inspector 的尺寸、位置、显示器和最大化状态；Inspector 标题保留 MesIngest Watch、窗口角色和当前 SeriesId 三层身份。

正式实现前用不连接 Host 的可运行 WPF 原型验证单屏窗口层级、焦点、任务栏、最小化、退出和屏幕恢复行为。原型 A 至 D 只作为设计证据，生产只采用 E；产品达到 E 的调查能力、非视觉回归通过并取得真实窗口预览批准后，以 Inspector 一次性替换旧内联详情，不保留用户功能开关或隐藏的第二套信息架构。
