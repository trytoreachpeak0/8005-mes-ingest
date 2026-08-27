namespace MesIngest.Watch;

internal abstract class WatchEmptyTextSection(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language)
{
    public override IReadOnlyList<WatchTextCatalogEntry> Entries { get; } =
        Array.Empty<WatchTextCatalogEntry>();
}

internal sealed partial class WatchOverviewText(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language);

internal sealed partial class WatchDemandSeriesText(WatchDisplayLanguage language)
    : WatchEmptyTextSection(language);

internal sealed partial class WatchReadabilityAuditText(WatchDisplayLanguage language)
    : WatchTextCatalogSection(language);

internal sealed partial class WatchErrorSearchText(WatchDisplayLanguage language)
    : WatchEmptyTextSection(language);

internal sealed partial class WatchAreaFilterText(WatchDisplayLanguage language)
    : WatchEmptyTextSection(language);

internal sealed partial class WatchCurrentAttentionText(WatchDisplayLanguage language)
    : WatchEmptyTextSection(language);

internal sealed partial class WatchInspectorText(WatchDisplayLanguage language)
    : WatchEmptyTextSection(language);

internal sealed partial class WatchFeedbackText(WatchDisplayLanguage language)
    : WatchEmptyTextSection(language);
