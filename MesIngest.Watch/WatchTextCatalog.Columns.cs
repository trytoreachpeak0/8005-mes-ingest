namespace MesIngest.Watch;

internal sealed class WatchColumnText(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language)
{
    private static WatchTextCatalogEntry E(string id, string zh, string en) =>
        new($"columns.{id}", zh, en);

    private static readonly WatchTextCatalogEntry SeriesIdEntry = E("seriesId", "需求系列标识", "SeriesId");
    private static readonly WatchTextCatalogEntry DemandIdEntry = E("demandId", "运输需求标识", "DemandId");
    private static readonly WatchTextCatalogEntry WorkTypeEntry = E("workType", "工序类型", "WorkType");
    private static readonly WatchTextCatalogEntry SublotEntry = E("sublot", "子批次", "SUBLOT");
    private static readonly WatchTextCatalogEntry AreaEntry = E("area", "区域", "AREA");
    private static readonly WatchTextCatalogEntry MesAreaEntry = E("mesArea", "制造执行系统区域", "MES AREA");
    private static readonly WatchTextCatalogEntry EqpEntry = E("eqp", "设备", "EQP");
    private static readonly WatchTextCatalogEntry StepEntry = E("step", "下一工序", "STEP");
    private static readonly WatchTextCatalogEntry SourceDateEntry = E("sourceDate", "来源时间", "MesSourceDate");
    private static readonly WatchTextCatalogEntry PackageEntry = E("package", "封装形式", "PACKAGE");
    private static readonly WatchTextCatalogEntry PollTraceEntry = E("pollTrace", "轮询追踪", "PollTrace");
    private static readonly WatchTextCatalogEntry ProjectionCommitEntry = E("projectionCommit", "投影提交", "ProjectionCommit");
    private static readonly WatchTextCatalogEntry DemandEntry = E("demand", "运输需求", "Demand");
    private static readonly WatchTextCatalogEntry TargetEntry = E("target", "目标", "Target");
    private static readonly WatchTextCatalogEntry DemandWorkTypeEntry = E("demandWorkType", "运输需求 / 工序类型", "Demand / WorkType");
    private static readonly WatchTextCatalogEntry SeriesEntry = E("series", "需求系列", "Series");
    private static readonly WatchTextCatalogEntry ObservationOrdinalEntry = E("observationOrdinal", "观测序号", "Observation ordinal");
    private static readonly WatchTextCatalogEntry SequenceEntry = E("sequence", "序列", "Sequence");
    private static readonly WatchTextCatalogEntry PollTraceSequenceEntry = E("pollTraceSequence", "轮询追踪序列", "PollTrace sequence");
    private static readonly WatchTextCatalogEntry PollTraceHighWaterEntry = E("pollTraceHighWater", "轮询追踪高水位", "PollTrace HighWater");
    private static readonly WatchTextCatalogEntry CatalogRevisionEntry = E("catalogRevision", "目录修订号", "Catalog Revision");
    private static readonly WatchTextCatalogEntry SnapshotEntry = E("snapshot", "快照", "Snapshot");
    private static readonly WatchTextCatalogEntry ProjectionEntry = E("projection", "投影", "Projection");
    private static readonly WatchTextCatalogEntry DatabaseEntry = E("database", "数据库", "Database");
    private static readonly WatchTextCatalogEntry VolumeEntry = E("volume", "卷", "Volume");
    private static readonly WatchTextCatalogEntry AvailableEntry = E("available", "可用", "Available");
    private static readonly WatchTextCatalogEntry DigestEntry = E("digest", "内容摘要", "Digest");
    private static readonly WatchTextCatalogEntry EvidenceEntry = E("evidence", "证据", "Evidence");
    private static readonly WatchTextCatalogEntry PhaseEntry = E("phase", "阶段", "Phase");
    private static readonly WatchTextCatalogEntry OutcomeEntry = E("outcome", "结果", "Outcome");
    private static readonly WatchTextCatalogEntry ErrorSearchAsOfEntry = E("errorSearchAsOf", "错误检索查询时点", "ErrorSearchAsOf");
    private static readonly WatchTextCatalogEntry QualificationCheckEntry = E("qualificationCheck", "服务端资格检查", "Host qualification check");

    private static readonly IReadOnlyList<WatchTextCatalogEntry> CatalogEntries =
    [
        SeriesIdEntry, DemandIdEntry, WorkTypeEntry, SublotEntry, AreaEntry,
        MesAreaEntry, EqpEntry, StepEntry, SourceDateEntry, PackageEntry,
        PollTraceEntry, ProjectionCommitEntry, DemandEntry, TargetEntry,
        DemandWorkTypeEntry, SeriesEntry, ObservationOrdinalEntry, SequenceEntry,
        PollTraceSequenceEntry, PollTraceHighWaterEntry, CatalogRevisionEntry,
        SnapshotEntry, ProjectionEntry, DatabaseEntry, VolumeEntry, AvailableEntry,
        DigestEntry, EvidenceEntry, PhaseEntry, OutcomeEntry, ErrorSearchAsOfEntry,
        QualificationCheckEntry,
    ];

    public override IReadOnlyList<WatchTextCatalogEntry> Entries => CatalogEntries;

    public string SeriesId => Text(SeriesIdEntry);
    public string DemandId => Text(DemandIdEntry);
    public string WorkType => Text(WorkTypeEntry);
    public string Sublot => Text(SublotEntry);
    public string Area => Text(AreaEntry);
    public string MesArea => Text(MesAreaEntry);
    public string Eqp => Text(EqpEntry);
    public string Step => Text(StepEntry);
    public string SourceDate => Text(SourceDateEntry);
    public string Package => Text(PackageEntry);
    public string PollTrace => Text(PollTraceEntry);
    public string ProjectionCommit => Text(ProjectionCommitEntry);
    public string Demand => Text(DemandEntry);
    public string Target => Text(TargetEntry);
    public string DemandWorkType => Text(DemandWorkTypeEntry);
    public string Series => Text(SeriesEntry);
    public string ObservationOrdinal => Text(ObservationOrdinalEntry);
    public string Sequence => Text(SequenceEntry);
    public string PollTraceSequence => Text(PollTraceSequenceEntry);
    public string PollTraceHighWater => Text(PollTraceHighWaterEntry);
    public string CatalogRevision => Text(CatalogRevisionEntry);
    public string Snapshot => Text(SnapshotEntry);
    public string Projection => Text(ProjectionEntry);
    public string Database => Text(DatabaseEntry);
    public string Volume => Text(VolumeEntry);
    public string Available => Text(AvailableEntry);
    public string Digest => Text(DigestEntry);
    public string Evidence => Text(EvidenceEntry);
    public string Phase => Text(PhaseEntry);
    public string Outcome => Text(OutcomeEntry);
    public string ErrorSearchAsOf => Text(ErrorSearchAsOfEntry);
    public string QualificationCheck => Text(QualificationCheckEntry);
}
