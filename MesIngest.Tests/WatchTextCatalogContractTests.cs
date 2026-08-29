using System.Reflection;
using System.Text.RegularExpressions;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed partial class WatchTextCatalogContractTests
{
    [Fact]
    public void Common_shell_and_settings_sections_expose_typed_bilingual_text_without_arbitrary_string_lookup()
    {
        var chinese = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var english = WatchTextCatalog.For(WatchDisplayLanguage.English);

        Assert.Equal(
            [WatchDisplayLanguage.SimplifiedChinese, WatchDisplayLanguage.English],
            Enum.GetValues<WatchDisplayLanguage>());
        Assert.Equal("简体中文", chinese.Common.SimplifiedChineseLanguageName);
        Assert.Equal("英语", chinese.Common.EnglishLanguageName);
        Assert.Equal("简体中文", english.Common.SimplifiedChineseLanguageName);
        Assert.Equal("English", english.Common.EnglishLanguageName);
        Assert.Equal("主导航", chinese.Shell.PrimaryNavigationName);
        Assert.Equal("Primary navigation", english.Shell.PrimaryNavigationName);
        Assert.Equal("设置", chinese.Settings.PageTitle);
        Assert.Equal("Settings", english.Settings.PageTitle);
        Assert.Equal("运输需求标识", chinese.Columns.DemandId);
        Assert.Equal("DemandId", english.Columns.DemandId);
        Assert.Equal("工序类型", chinese.Columns.WorkType);
        Assert.Equal("WorkType", english.Columns.WorkType);
        Assert.Equal("制造执行系统接入运维台 · 需求系列调查窗口", chinese.Inspector.AppTitle);
        Assert.Equal("MesIngest Watch · Demand series Inspector", english.Inspector.AppTitle);

        var sectionProperties = typeof(WatchTextCatalog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(IWatchTextCatalogSection).IsAssignableFrom(property.PropertyType))
            .ToArray();
        Assert.Equal(
            [
                "AreaFilter",
                "Columns",
                "Common",
                "CurrentAttention",
                "DemandSeries",
                "ErrorSearch",
                "Feedback",
                "Inspector",
                "Overview",
                "ReadabilityAudit",
                "Settings",
                "Shell",
            ],
            sectionProperties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        var publicMembers = sectionProperties
            .SelectMany(property => property.PropertyType.GetMembers(BindingFlags.Public | BindingFlags.Instance))
            .Concat(typeof(WatchTextCatalog).GetMembers(BindingFlags.Public | BindingFlags.Instance))
            .ToArray();
        Assert.DoesNotContain(
            publicMembers.OfType<MethodInfo>(),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string))
                && method.Name is "Get" or "Lookup" or "Resolve" or "Pick");
        Assert.DoesNotContain(
            typeof(WatchTextCatalog).Assembly.GetTypes()
                .SelectMany(type => type.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)),
            method => method.Name == "Pick");
        Assert.DoesNotContain(
            typeof(WatchFeedbackText).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
            method => method.ReturnType == typeof(WatchLocalizedText)
                && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(string)));
        Assert.DoesNotContain(
            publicMembers.OfType<PropertyInfo>(),
            property => property.GetIndexParameters().Any(parameter => parameter.ParameterType == typeof(string)));
        Assert.DoesNotContain(
            typeof(WatchTextCatalog).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("FluentPrototype", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void Simplified_chinese_catalog_uses_chinese_for_all_static_ui_vocabulary()
    {
        var forbiddenTokens = new[]
        {
            "MesIngest Watch", "Host", "Watch", "Inspector", "DemandSeries",
            "TransportDemand", "DemandId", "SeriesId", "WorkType", "SUBLOT",
            "AREA", "EQP", "STEP", "DATES", "PACKAGE", "PollTrace",
            "ProjectionCommit", "CatalogRevision", "SnapshotReference",
            "LiveMesFieldSet", "ErrorSearchAsOf", "Error Search", "Endpoint",
            "canonical", "fingerprint incident", "Demand Generation", "Windows",
            "English", "JSON", "API", "TXT", "UI", "rail", "epx",
            "Tracking", "Archived", "Dispatch", "Digest", "Catalog", "Assignment",
            "SeriesSequence", "EventId", "OccurredAt", "EventType",
            "SubjectKind", "SubjectId", "PayloadVersion", "PayloadJson",
        };

        Assert.All(WatchTextCatalog.AllEntries, entry =>
        {
            Assert.All(forbiddenTokens, token => Assert.DoesNotMatch(
                $"(?<![A-Za-z0-9_]){Regex.Escape(token)}(?![A-Za-z0-9_])",
                entry.SimplifiedChinese));
        });
    }

    [Fact]
    public void All_catalog_entries_are_nonempty_bilingual_and_have_matching_format_parameters()
    {
        var entries = WatchTextCatalog.AllEntries;
        var catalog = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var sectionEntries = typeof(WatchTextCatalog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(IWatchTextCatalogSection).IsAssignableFrom(property.PropertyType))
            .Select(property => Assert.IsAssignableFrom<IWatchTextCatalogSection>(property.GetValue(catalog)))
            .SelectMany(section => section.Entries)
            .ToArray();

        Assert.NotEmpty(entries);
        Assert.Equal(
            sectionEntries.Select(entry => entry.SemanticId).Order(StringComparer.Ordinal).ToArray(),
            entries.Select(entry => entry.SemanticId).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(entries.Count, entries.Select(entry => entry.SemanticId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.SemanticId));
            Assert.False(string.IsNullOrWhiteSpace(entry.SimplifiedChinese));
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.NotEqual(entry.SemanticId, entry.SimplifiedChinese);
            Assert.NotEqual(entry.SemanticId, entry.English);
            Assert.Equal(FormatParameters(entry.SimplifiedChinese), FormatParameters(entry.English));
        });
    }

    [Fact]
    public void Typed_feedback_members_remain_bound_to_their_named_catalog_entries()
    {
        var expectedEnglish = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(WatchFeedbackText.InformationSeverity)] = "Information",
            [nameof(WatchFeedbackText.SuccessSeverity)] = "Success",
            [nameof(WatchFeedbackText.ErrorSeverity)] = "Error",
            [nameof(WatchFeedbackText.SettingsSaved)] = "Local settings saved",
            [nameof(WatchFeedbackText.SettingsSavedDetail)] = "Auto-refresh remains enabled; the current Host session was not rebuilt.",
            [nameof(WatchFeedbackText.SettingsSaveFailed)] = "Could not save local settings",
            [nameof(WatchFeedbackText.SettingsSaveFailedDetail)] = "The local settings file could not be written. Check file permissions and try again.",
            [nameof(WatchFeedbackText.RetrySaveAction)] = "Retry save",
            [nameof(WatchFeedbackText.RetryAction)] = "Retry",
            [nameof(WatchFeedbackText.SelectionCleared)] = "Refresh cleared the previous selection; details remain unselected. Select an item again.",
            [nameof(WatchFeedbackText.DemandSeriesSelectionCleared)] = "The previous demand series is no longer in the refreshed results",
            [nameof(WatchFeedbackText.TransportDemandSelectionCleared)] = "The previous transport demand is no longer in the refreshed results",
            [nameof(WatchFeedbackText.ErrorSelectionCleared)] = "The previous error item is no longer in the refreshed results",
            [nameof(WatchFeedbackText.AttentionSelectionCleared)] = "The previous attention item is no longer in the refreshed results",
            [nameof(WatchFeedbackText.ReadabilityOperationTitle)] = "Unable to complete the eligibility-audit operation",
            [nameof(WatchFeedbackText.ReadabilityOperationAction)] = "Return to eligibility audit",
            [nameof(WatchFeedbackText.DemandSeriesOperationTitle)] = "Unable to complete the demand-series operation",
            [nameof(WatchFeedbackText.DemandSeriesOperationAction)] = "Return to demand series",
            [nameof(WatchFeedbackText.OperationRetryMessage)] = "Check the input or current snapshot and try again.",
            [nameof(WatchFeedbackText.HostAppliedTitle)] = "Host settings applied",
            [nameof(WatchFeedbackText.HostAppliedMessage)] = "The contract is compatible; overview was read and auto-refresh resumed.",
            [nameof(WatchFeedbackText.HostFailedTitle)] = "Unable to apply Host settings",
            [nameof(WatchFeedbackText.HostFailedMessage)] = "Check the address format, timeout range, or local settings file and try again.",
            [nameof(WatchFeedbackText.LayoutRestoredTitle)] = "Default layout restored",
            [nameof(WatchFeedbackText.LayoutRestoredMessage)] = "The window was restored to 1440×900 with a compact navigation rail; refresh intervals and the Host session are unchanged.",
            [nameof(WatchFeedbackText.LayoutFailedTitle)] = "Unable to restore the default layout",
            [nameof(WatchFeedbackText.LayoutFailedMessage)] = "The local layout settings could not be written. Check file permissions and try again.",
            [nameof(WatchFeedbackText.DefaultsRestoredTitle)] = "Default settings restored",
            [nameof(WatchFeedbackText.DefaultsRestoredMessage)] = "The window was restored to 1440×900 with a compact navigation rail; all five data views keep 10-second auto-refresh. The Host session was not rebuilt.",
            [nameof(WatchFeedbackText.DefaultsFailedTitle)] = "Unable to restore default settings",
            [nameof(WatchFeedbackText.DefaultsFailedMessage)] = "The local settings could not be written. Check file permissions and try again.",
        };
        var typedProperties = typeof(WatchFeedbackText)
            .GetProperties(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(property => property.PropertyType == typeof(WatchLocalizedText))
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

        Assert.Equal(expectedEnglish.Keys.Order(), typedProperties.Keys.Order());
        Assert.All(expectedEnglish, pair =>
        {
            var localized = Assert.IsType<WatchLocalizedText>(
                typedProperties[pair.Key].GetValue(null));
            Assert.Equal(pair.Value, localized.English);
            Assert.False(string.IsNullOrWhiteSpace(localized.SimplifiedChinese));
        });
    }

    [Fact]
    public void Production_visible_copy_cannot_bypass_typed_enumerable_catalog_entries()
    {
        var watchDirectory = FindWatchSourceDirectory();
        var sourceFiles = Directory.GetFiles(watchDirectory, "*.cs", SearchOption.TopDirectoryOnly);
        var sources = sourceFiles.ToDictionary(
            path => path,
            File.ReadAllText,
            StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(sources, pair => pair.Value.Contains(
            "new WatchNotificationEvent(",
            StringComparison.Ordinal));
        Assert.DoesNotContain(sources, pair => pair.Value.Contains(
            "TranslateKnownChinese",
            StringComparison.Ordinal));
        Assert.DoesNotContain(sources, pair => pair.Value.Contains(
            ".Pick(",
            StringComparison.Ordinal));
        Assert.DoesNotContain(sources, pair => pair.Value.Contains(
            "WatchFeedbackText.Localized(",
            StringComparison.Ordinal));
        var productionNotificationSource = sources.Single(pair => string.Equals(
            Path.GetFileName(pair.Key),
            "WatchWorkspaceWindow.Notifications.cs",
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            "LocalizedContent = WatchFeedbackText",
            productionNotificationSource.Value,
            StringComparison.Ordinal);

        var productionSources = sources
            .Where(pair => !Path.GetFileName(pair.Key).StartsWith(
                "WatchTextCatalog.",
                StringComparison.Ordinal))
            .ToArray();
        var forbiddenMonolingualEscapes = new[]
        {
            "概览快照、客户端读取时间与自动刷新策略",
            "正在验证新 Host 契约",
            "展开或折叠主导航",
            "最小化窗口",
            "资格审计紧凑快照事实",
            "当前冻结审计快照中的全部外部可见资格检查通过",
        };
        Assert.All(forbiddenMonolingualEscapes, forbidden =>
            Assert.DoesNotContain(
                productionSources,
                pair => pair.Value.Contains(forbidden, StringComparison.Ordinal)));

        var runtimePresentationBypass = new Regex(
            "(?:DisplayText|AutomationName)\\s*=>\\s*\\$?\"[^\"]*[\\p{IsCJKUnifiedIdeographs}]"
            + "|AutomationProperties\\.SetName\\(\\s*[^,]+,\\s*\\$?\"[^\"]*[\\p{IsCJKUnifiedIdeographs}]"
            + "|BeginAreaProfileFileOperation\\(\\s*[^,]+,\\s*\"[^\"]*[\\p{IsCJKUnifiedIdeographs}]"
            + "|(?:Tracking|Archived)\\s+—",
            RegexOptions.CultureInvariant);
        var runtimePresentationFiles = productionSources.Where(pair =>
            Path.GetFileName(pair.Key) is
                "WatchWorkspaceWindow.AreaSelectors.cs"
                or "WatchWorkspaceWindow.AreaProfiles.cs"
                or "WatchWorkspaceWindow.xaml.cs");
        Assert.DoesNotContain(
            runtimePresentationFiles,
            pair => runtimePresentationBypass.IsMatch(pair.Value));

        var workspaceXaml = File.ReadAllText(Path.Combine(
            watchDirectory,
            "WatchWorkspaceWindow.xaml"));
        Assert.DoesNotMatch(
            "x:Name=\"AreaProfileRowValidStatus\"[\\s\\S]{0,160}?Text=\"有效\"",
            workspaceXaml);
        Assert.Contains(
            "x:Name=\"AreaProfileRowValidStatus\"",
            workspaceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Text=\"{Binding ValidityText}\"",
            workspaceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding ProfileIdentity}\"",
            workspaceXaml,
            StringComparison.Ordinal);
        var areaProfileSource = sources.Single(pair => string.Equals(
            Path.GetFileName(pair.Key),
            "WatchWorkspaceWindow.AreaProfiles.cs",
            StringComparison.Ordinal));
        Assert.DoesNotContain(
            "AllAreasProfileDisplayName",
            areaProfileSource.Value,
            StringComparison.Ordinal);

        var inlineLocalizedLiteral = new Regex(
            "new\\s*(?:WatchLocalizedText)?\\s*\\(\\s*\"[^\"]*[\\p{IsCJKUnifiedIdeographs}][^\"]*\"\\s*,\\s*\"",
            RegexOptions.CultureInvariant);
        Assert.DoesNotContain(sources, pair => inlineLocalizedLiteral.IsMatch(pair.Value));

        const string fundamentalLanguageSelection = "Language == WatchDisplayLanguage.SimplifiedChinese";
        var catalogBaseSource = sources.Single(pair => string.Equals(
            Path.GetFileName(pair.Key),
            "WatchTextCatalog.cs",
            StringComparison.Ordinal));
        Assert.Equal(
            2,
            Regex.Matches(
                catalogBaseSource.Value,
                Regex.Escape(fundamentalLanguageSelection),
                RegexOptions.CultureInvariant).Count);
        var catalogSectionSources = sources
            .Where(pair => Path.GetFileName(pair.Key).StartsWith(
                "WatchTextCatalog.",
                StringComparison.Ordinal)
                && !Path.GetFileName(pair.Key).EndsWith(
                    ".Generated.cs",
                    StringComparison.Ordinal))
            .Select(pair => string.Equals(
                    Path.GetFileName(pair.Key),
                    "WatchTextCatalog.cs",
                    StringComparison.Ordinal)
                ? new KeyValuePair<string, string>(
                    pair.Key,
                    new Regex(
                        Regex.Escape(fundamentalLanguageSelection),
                        RegexOptions.CultureInvariant)
                        .Replace(pair.Value, string.Empty, count: 2))
                : pair)
            .ToArray();
        Assert.DoesNotContain(
            catalogSectionSources,
            pair => pair.Value.Contains(
                "Language == WatchDisplayLanguage",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            catalogSectionSources,
            pair => pair.Value.Contains("(Language,", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_code_description_is_localized_and_preserves_the_raw_code()
    {
        const string rawCode = "FUTURE_CONTRACT_CODE_17";

        var chinese = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese)
            .DescribeUnknownCode(rawCode);
        var english = WatchTextCatalog.For(WatchDisplayLanguage.English)
            .DescribeUnknownCode(rawCode);

        Assert.Equal(rawCode, chinese.RawCode);
        Assert.Equal(rawCode, english.RawCode);
        Assert.False(chinese.IsKnown);
        Assert.False(english.IsKnown);
        Assert.NotEqual(chinese.Description, english.Description);
        Assert.Contains("未知", chinese.Description, StringComparison.Ordinal);
        Assert.Contains("unknown", english.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connected", english.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Simplified_chinese_normal_ui_uses_chinese_only_for_known_codes_and_preserves_unknown_codes()
    {
        var normalized = new WatchTextCatalogEntry(
            "test.known-codes",
            "GONE · ACTIVE · UTC · NULL · HistoryReset",
            "GONE · ACTIVE · UTC · NULL · HistoryReset");
        var chinese = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var english = WatchTextCatalog.For(WatchDisplayLanguage.English);

        Assert.Equal("已消失 · 活动 · 协调世界时 · 空值 · 历史重置", normalized.SimplifiedChinese);
        Assert.Equal("跟踪中", chinese.DemandSeries.DescribeLifecycle("TRACKING"));
        Assert.Equal("活动中", chinese.ErrorSearch.CodeWithMeaning(
            chinese.ErrorSearch.DescribeActivityState("ACTIVE")));
        Assert.Equal("活动需求系列错误", chinese.CurrentAttention.CodeWithMeaning(
            chinese.CurrentAttention.DescribeKind("SERIES_ERROR")));
        Assert.Equal("不可读", chinese.ReadabilityAudit.CodeWithMeaning(
            chinese.ReadabilityAudit.DescribeReadabilityMeaning("NOT_READABLE")));

        Assert.Equal("Tracking (TRACKING)", english.DemandSeries.DescribeLifecycle("TRACKING"));
        Assert.Equal("Active · ACTIVE", english.ErrorSearch.CodeWithMeaning(
            english.ErrorSearch.DescribeActivityState("ACTIVE")));
        Assert.Contains(
            "FUTURE_ACTIVITY",
            chinese.ErrorSearch.CodeWithMeaning(
                chinese.ErrorSearch.DescribeActivityState("FUTURE_ACTIVITY")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_value_semantics_keep_all_six_missing_query_states_distinct_in_both_languages()
    {
        var missingStates = new[]
        {
            WatchDisplayValue.SourceNotProvided,
            WatchDisplayValue.SystemUnknown,
            WatchDisplayValue.NotApplicable,
            WatchDisplayValue.NotLoaded,
            WatchDisplayValue.EmptyResult,
            WatchDisplayValue.ReadFailed,
        };

        foreach (var language in Enum.GetValues<WatchDisplayLanguage>())
        {
            var catalog = WatchTextCatalog.For(language);
            var rendered = missingStates.Select(catalog.RenderValue).ToArray();

            Assert.All(rendered, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.Equal(missingStates.Length, rendered.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(rendered, text => text == "—");
            Assert.Equal("RAW-VALUE-17", catalog.RenderValue(WatchDisplayValue.Present("RAW-VALUE-17")));
        }
    }

    [Fact]
    public void Absolute_time_uses_system_local_zone_while_relative_time_and_count_follow_display_language()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-27T06:05:06+00:00");
        var now = observedAt.AddSeconds(18);
        var chinese = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var english = WatchTextCatalog.For(WatchDisplayLanguage.English);
        var expectedAbsoluteTime = WatchTimeDisplay.Format(observedAt);

        Assert.Equal(expectedAbsoluteTime, chinese.FormatAbsoluteTime(observedAt));
        Assert.Equal(expectedAbsoluteTime, english.FormatAbsoluteTime(observedAt));
        Assert.Contains("18", chinese.FormatRelativeTime(observedAt, now), StringComparison.Ordinal);
        Assert.Contains("18", english.FormatRelativeTime(observedAt, now), StringComparison.Ordinal);
        Assert.NotEqual(
            chinese.FormatRelativeTime(observedAt, now),
            english.FormatRelativeTime(observedAt, now));
        Assert.Equal("1,234 项", chinese.FormatCount(1234, WatchCountUnit.Items));
        Assert.Equal("1,234 items", english.FormatCount(1234, WatchCountUnit.Items));
    }

    private static string[] FormatParameters(string text) =>
        FormatParameterRegex()
            .Matches(text)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindWatchSourceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "MesIngest.Watch");
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, "WatchTextCatalog.cs")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the MesIngest.Watch production source directory.");
    }

    [GeneratedRegex("\\{([^{}:,]+)[,:}]", RegexOptions.CultureInvariant)]
    private static partial Regex FormatParameterRegex();
}
