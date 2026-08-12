using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MesIngest.Watch.FluentPrototype;

public partial class MainWindow
{
    public MainWindow(string initialVariant, string initialPage, string scenario)
    {
        InitializeComponent();
        var data = new PrototypeData();
        DataContext = data;
        SelectedPrototype.SelectAreaVariant(initialVariant);
        SelectedPrototype.SelectErrorVariant(initialVariant);
        SelectedPrototype.SelectOverviewVariant(initialVariant);
        SelectedPrototype.Navigate(initialPage);
        SelectedPrototype.SetNavigationPane(scenario.Contains("nav-open", StringComparison.OrdinalIgnoreCase));
        SelectedPrototype.SetErrorReviewSwitcherVisible(!scenario.Contains("clean", StringComparison.OrdinalIgnoreCase));
        SelectedPrototype.SetOverviewReviewSwitcherVisible(!scenario.Contains("clean", StringComparison.OrdinalIgnoreCase));
        SelectedPrototype.ApplyOverviewScenario(scenario);
    }

    public void Capture(string path, int pixelWidth, int pixelHeight)
    {
        var absolutePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        CaptureRoot.Width = pixelWidth;
        CaptureRoot.Height = pixelHeight;
        CaptureRoot.Measure(new Size(pixelWidth, pixelHeight));
        CaptureRoot.Arrange(new Rect(0, 0, pixelWidth, pixelHeight));
        CaptureRoot.UpdateLayout();
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(CaptureRoot);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(absolutePath);
        encoder.Save(stream);
    }

}

public sealed class PrototypeData
{
    public ObservableCollection<AlertRow> Alerts { get; } =
    [
        new("alert-field-drift-004", "journey-visible-001", "ERROR", "FIELD_DRIFT", "WIRE_TO_GATE", "SLOT-LOT-001", "冻结字段 STEP 从 焊线 变为 焊线2；保留原投影。", "12:41:52", 4, "精确 DemandId"),
        new("alert-zero-drop-002", "—", "ERROR", "PAUSED_ZERO_DROP", "DIE_TO_OVEN", "—", "健康非零基线骤降为零；该类型 GONE 判定已暂停。", "12:40:18", 2, "TASK_TYPE 范围"),
        new("alert-reappear-001", "journey-visible-003", "WARNING", "REAPPEAR_AFTER_GONE", "STAGING_TO_WIRE", "B240811-07", "同一业务键重新出现；已创建新的任务实例。", "12:36:09", 1, "前后两个 DemandId"),
    ];

    public ObservableCollection<TaskRow> Tasks { get; } =
    [
        new("journey-visible-001", "WIRE_TO_GATE", "SLOT-LOT-001", "A01", "WB-01", "质检", "VISIBLE", "12:42:14"),
        new("journey-visible-002", "DIE_TO_OVEN", "B240811-03", "D02", "DA-07", "烘箱", "VISIBLE", "12:42:14"),
        new("journey-visible-003", "STAGING_TO_WIRE", "B240811-07", "W01", "WB-12", "焊线", "VISIBLE", "12:42:13"),
        new("journey-visible-004", "WIRE_TO_OPTICAL", "B240811-11", "W02", "WB-05", "三光", "VISIBLE", "12:42:13"),
        new("journey-visible-005", "WIRE_TO_NITROGEN", "B240811-14", "W01", "WB1-03", "焊线2", "VISIBLE", "12:42:12"),
    ];

    public ObservableCollection<TaskRow> GoneTasks { get; } =
    [
        new("journey-gone-008", "WIRE_TO_GATE", "SLOT-LOT-001", "A01", "WB-03", "质检", "GONE", "12:31:08"),
        new("journey-gone-019", "DIE_TO_OVEN", "B240810-19", "D02", "DA-02", "烘箱", "GONE", "12:28:44"),
    ];

    public ObservableCollection<TaskTypeCount> TaskTypeCounts { get; } =
    [
        new("WIRE_TO_GATE", 38),
        new("DIE_TO_OVEN", 24),
        new("STAGING_TO_WIRE", 19),
        new("WIRE_TO_OPTICAL", 12),
        new("WIRE_TO_NITROGEN", 7),
    ];

    public ObservableCollection<CatalogDemandRow> CatalogDemands { get; } =
    [
        new("D-001842", "WIRE_TO_GATE", "SLOT-LOT-001", "A1-1", "WB-01", "质检", "可读", "—", 9, "12:42:14"),
        new("D-001843", "DIE_TO_OVEN", "B240811-03", "D2-7", "DA-07", "烘箱", "可读", "—", 3, "12:42:14"),
        new("D-001844", "STAGING_TO_WIRE", "B240811-07", "W1-12", "WB-12", "焊线", "不可见", "DEMAND_GONE", 5, "10:04:48"),
        new("D-002044", "STAGING_TO_WIRE", "B240811-07", "W1-12", "WB-12", "焊线", "可读", "—", 4, "12:42:13"),
        new("D-001845", "WIRE_TO_OPTICAL", "B240811-11", "W2-5", "WB-05", "三光", "不可见", "DEMAND_GONE", 2, "12:30:06"),
        new("D-001846", "DIE_TO_OVEN", "B240811-19", "<NULL>", "DA-02", "烘箱", "阻断", "EMPTY_MES_FIELD · AREA", 4, "12:42:12"),
        new("D-001847", "WIRE_TO_GATE", "B240811-22", "A1-3", "WB-03", "质检", "阻断", "DUPLICATE_TRANSPORT_DEMAND_KEY", 6, "12:42:12"),
        new("D-001848", "WIRE_TO_NITROGEN", "B240811-24", "N1-3", "WB1-03", "焊线2", "阻断", "SUBLOT_MULTIPLE_WORK_TYPES", 2, "12:42:11"),
        new("D-002151", "WIRE_TO_NITROGEN", "B240810-09", "N1-2", "WB1-02", "焊线2", "不可见", "DEMAND_SERIES_ARCHIVED", 1, "12:42:11"),
    ];

    public ObservableCollection<CatalogDemandRow> ReadableCatalog { get; } =
    [
        new("D-001842", "WIRE_TO_GATE", "SLOT-LOT-001", "A1-1", "WB-01", "质检", "可读", "—", 9, "12:42:14"),
        new("D-001843", "DIE_TO_OVEN", "B240811-03", "D2-7", "DA-07", "烘箱", "可读", "—", 3, "12:42:14"),
        new("D-002044", "STAGING_TO_WIRE", "B240811-07", "W1-12", "WB-12", "焊线", "可读", "—", 4, "12:42:13"),
        new("D-001845", "WIRE_TO_OPTICAL", "B240811-11", "W2-5", "WB-05", "三光", "可读", "—", 2, "12:42:13"),
    ];

    public ObservableCollection<CatalogDemandRow> AreaFilteredCatalog { get; } =
    [
        new("D-001842", "WIRE_TO_GATE", "SLOT-LOT-001", "A1-1", "WB-01", "质检", "可读", "—", 9, "12:42:14"),
        new("D-001843", "DIE_TO_OVEN", "B240811-03", "D2-7", "DA-07", "烘箱", "可读", "—", 3, "12:42:14"),
        new("D-001844", "STAGING_TO_WIRE", "B240811-07", "W1-12", "WB-12", "焊线", "不可见", "DEMAND_GONE", 5, "10:04:48"),
        new("D-002044", "STAGING_TO_WIRE", "B240811-07", "W1-12", "WB-12", "焊线", "可读", "—", 4, "12:42:13"),
        new("D-001845", "WIRE_TO_OPTICAL", "B240811-11", "W2-5", "WB-05", "三光", "不可见", "DEMAND_GONE", 2, "12:30:06"),
        new("D-001848", "WIRE_TO_NITROGEN", "B240811-24", "N1-3", "WB1-03", "焊线2", "阻断", "SUBLOT_MULTIPLE_WORK_TYPES", 2, "12:42:11"),
        new("D-002151", "WIRE_TO_NITROGEN", "B240810-09", "N1-2", "WB1-02", "焊线2", "不可见", "DEMAND_SERIES_ARCHIVED", 1, "12:42:11"),
    ];

    public ObservableCollection<CatalogTypeCount> CatalogTypeCounts { get; } =
    [
        new("WIRE_TO_GATE", 492, 418, 74),
        new("DIE_TO_OVEN", 438, 356, 82),
        new("STAGING_TO_WIRE", 421, 337, 84),
        new("WIRE_TO_OPTICAL", 386, 314, 72),
        new("WIRE_TO_NITROGEN", 334, 274, 60),
        new("DIE_TO_WIRE_STAGING", 276, 143, 133),
    ];

    public ObservableCollection<DemandSeriesRow> DemandSeries { get; } =
    [
        new("DS-000842", "WIRE_TO_GATE", "SLOT-LOT-001", "Tracking", "VISIBLE", "A1-1", "D-001842", 1, 9, "08-11 12:32", "12:42:14", "—", "—"),
        new("DS-000843", "DIE_TO_OVEN", "B240811-03", "Tracking", "VISIBLE", "D2-7", "D-001843", 1, 6, "08-11 12:34", "12:42:14", "—", "—"),
        new("DS-000844", "STAGING_TO_WIRE", "B240811-07", "Tracking", "VISIBLE", "W1-12", "D-002044", 2, 9, "08-11 09:12", "12:42:13", "—", "—"),
        new("DS-000845", "WIRE_TO_OPTICAL", "B240811-11", "Tracking", "GONE", "W2-5", "D-001845", 1, 8, "08-11 08:42", "12:30:06", "12:36:09", "—"),
        new("DS-000611", "DIE_TO_WIRE_STAGING", "B240810-18", "Archived", "GONE", "D3-2", "D-001104", 1, 11, "08-10 07:18", "08-10 18:02", "08-10 18:08", "08-11 06:12"),
        new("DS-000577", "WIRE_TO_NITROGEN", "B240810-09", "Archived", "VISIBLE_AFTER_ARCHIVE", "N1-2", "D-002151", 2, 17, "08-10 02:11", "12:42:11", "08-10 14:26", "08-11 02:30"),
    ];

    public ObservableCollection<SeriesDemandRow> SelectedSeriesDemands { get; } =
    [
        new("D-001844", 1, "GONE", "08-11 09:12:03", "08-11 10:04:48", "08-11 10:10:55", "否"),
        new("D-002044", 2, "VISIBLE", "08-11 10:18:22", "12:42:13", "—", "是"),
    ];

    public ObservableCollection<SeriesEventRow> SelectedSeriesEvents { get; } =
    [
        new(1, "DEMAND_SERIES_STARTED", "08-11 09:12:03", "—", "首次观察到 STAGING_TO_WIRE + B240811-07。"),
        new(2, "TRANSPORT_DEMAND_CREATED", "08-11 09:12:03", "D-001844", "创建第 1 代 Demand；当前唯一原始观测。"),
        new(3, "MES_FIELD_CHANGED", "08-11 09:46:18", "D-001844", "PACKAGE 从 QFN48 更新为 QFN64。"),
        new(4, "DEMAND_BECAME_EXTERNALLY_READABLE", "08-11 09:46:18", "D-001844", "当前条件全部满足，进入外部可读目录。"),
        new(5, "DEMAND_GONE", "08-11 10:10:55", "D-001844", "完整成功轮次确认缺席；Demand 转为 GONE。"),
        new(6, "TRANSPORT_DEMAND_CREATED", "08-11 10:18:22", "D-002044", "同一业务键再次出现，创建第 2 代 Demand。"),
        new(7, "SUBLOT_MULTIPLE_WORK_TYPES_OBSERVED", "08-11 10:18:22", "D-002044", "同一 SUBLOT 同轮还命中 WIRE_TO_GATE，当前阻断外部读取。"),
        new(8, "SUBLOT_MULTIPLE_WORK_TYPES_CLEARED", "08-11 10:24:19", "D-002044", "跨 WorkType 条件消失。"),
        new(9, "DEMAND_BECAME_EXTERNALLY_READABLE", "08-11 10:24:19", "D-002044", "第 2 代 Demand 进入 Revision 184 的目录。"),
    ];

    public ObservableCollection<ErrorCategoryRow> ErrorCategories { get; } =
    [
        new("数据格式", 4, 31, 7, "字段为空、AREA 格式非法或值无法解析"),
        new("一致性冲突", 3, 18, 5, "重复业务键、同批次多工序或观测不一致"),
        new("生命周期", 3, 12, 2, "GONE 后重现、归档后仍可见等时序异常"),
        new("源数据漂移", 2, 9, 4, "冻结字段在同一 Demand 世代内发生变化"),
        new("接入质量", 2, 6, 1, "健康基线骤降、轮次不完整或采集暂停"),
    ];

    public ObservableCollection<ErrorSeriesRow> ErrorSeries { get; } =
    [
        new("DS-000918", "WIRE_TO_GATE", "B240812-17", "A01-01", "MES_AREA_FORMAT_INVALID", "活动中", 6, "D-002311 · 第 2 代", "08-12 08:14", "12:41:52"),
        new("DS-000844", "STAGING_TO_WIRE", "B240811-07", "W1-12", "EMPTY_MES_FIELD", "已恢复", 3, "D-001844 · 第 1 代", "08-11 09:12", "08-11 10:24"),
        new("DS-000901", "DIE_TO_OVEN", "B240812-03", "<NULL>", "EMPTY_MES_FIELD", "活动中", 2, "D-002284 · 第 1 代", "08-12 07:42", "12:40:18"),
        new("DS-000733", "WIRE_TO_OPTICAL", "B240811-22", "W2-5", "MES_AREA_FORMAT_INVALID", "已恢复", 1, "D-001992 · 第 1 代", "08-11 14:06", "08-11 14:18"),
        new("DS-000577", "WIRE_TO_NITROGEN", "B240810-09", "N1-2", "FIELD_VALUE_PARSE_FAILED", "已恢复", 4, "D-002151 · 第 2 代", "08-10 11:33", "08-10 12:02"),
    ];

    public ObservableCollection<ErrorEvidenceRow> ErrorEvidence { get; } =
    [
        new("#1842", "12:41:52", "D-002311", "第 2 代", "MES_AREA_FORMAT_INVALID", "AREA 原始值 A01-01 不符合 MesArea 格式；外部可读资格已阻断。"),
        new("#1838", "12:32:09", "D-002311", "第 2 代", "MES_AREA_FORMAT_INVALID", "连续成功轮次仍观察到非法 AREA 原始值 A01-01。"),
        new("#1804", "08:14:27", "D-002297", "第 1 代", "MES_AREA_FORMAT_INVALID", "首次发现非法 AREA；保留原始观测且未静默规范化。"),
    ];
}

public sealed record AlertRow(
    string AlertId,
    string DemandId,
    string Severity,
    string Code,
    string TaskType,
    string Sublot,
    string Summary,
    string LastSeen,
    int Occurrences,
    string Scope);

public sealed record TaskRow(
    string DemandId,
    string TaskType,
    string Sublot,
    string Area,
    string Eqp,
    string Step,
    string Status,
    string LastSeen);

public sealed record TaskTypeCount(string TaskType, int Count);

public sealed record CatalogDemandRow(
    string DemandId,
    string WorkType,
    string Sublot,
    string Area,
    string Eqp,
    string Step,
    string Readability,
    string Blocker,
    int DemandRevision,
    string LastSeen);

public sealed record CatalogTypeCount(
    string WorkType,
    int Visible,
    int Readable,
    int Blocked);

public sealed record DemandSeriesRow(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string Presence,
    string CurrentArea,
    string CurrentDemandId,
    int DemandCount,
    int EventCount,
    string StartedAt,
    string LastSeenAt,
    string GoneSince,
    string ArchivedAt);

public sealed record SeriesDemandRow(
    string DemandId,
    int Generation,
    string Status,
    string CreatedAt,
    string LastSeenAt,
    string GoneAt,
    string CurrentReadable);

public sealed record SeriesEventRow(
    int Sequence,
    string EventKind,
    string ObservedAt,
    string DemandId,
    string Summary);

public sealed record ErrorCategoryRow(
    string Category,
    int CodeCount,
    int SeriesCount,
    int ActiveCount,
    string Description);

public sealed record ErrorSeriesRow(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Area,
    string ErrorCode,
    string ErrorState,
    int Occurrences,
    string AffectedDemand,
    string FirstSeen,
    string LastSeen);

public sealed record ErrorEvidenceRow(
    string Revision,
    string ObservedAt,
    string DemandId,
    string Generation,
    string ErrorCode,
    string Summary);

public sealed class AreaProfileRow
{
    public string Name { get; set; } = "";
    public int AreaCount { get; set; }
    public string State { get; set; } = "";
    public string Modified { get; set; } = "";
    public string Scope { get; set; } = "";
}
