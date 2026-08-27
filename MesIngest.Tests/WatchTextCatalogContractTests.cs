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
        Assert.Equal("English", chinese.Common.EnglishLanguageName);
        Assert.Equal("简体中文", english.Common.SimplifiedChineseLanguageName);
        Assert.Equal("English", english.Common.EnglishLanguageName);
        Assert.Equal("主导航", chinese.Shell.PrimaryNavigationName);
        Assert.Equal("Primary navigation", english.Shell.PrimaryNavigationName);
        Assert.Equal("设置", chinese.Settings.PageTitle);
        Assert.Equal("Settings", english.Settings.PageTitle);

        var sectionProperties = typeof(WatchTextCatalog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => typeof(IWatchTextCatalogSection).IsAssignableFrom(property.PropertyType))
            .ToArray();
        Assert.Equal(
            [
                "AreaFilter",
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
            publicMembers.OfType<PropertyInfo>(),
            property => property.GetIndexParameters().Any(parameter => parameter.ParameterType == typeof(string)));
        Assert.DoesNotContain(
            typeof(WatchTextCatalog).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("FluentPrototype", StringComparison.OrdinalIgnoreCase) == true);
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
    public void Absolute_time_keeps_offset_while_relative_time_and_count_follow_display_language()
    {
        var observedAt = DateTimeOffset.Parse("2026-08-27T14:05:06+08:00");
        var now = observedAt.AddSeconds(18);
        var chinese = WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese);
        var english = WatchTextCatalog.For(WatchDisplayLanguage.English);

        Assert.Equal("2026-08-27 14:05:06 +08:00", chinese.FormatAbsoluteTime(observedAt));
        Assert.Equal("2026-08-27 14:05:06 +08:00", english.FormatAbsoluteTime(observedAt));
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

    [GeneratedRegex("\\{([^{}:,]+)[,:}]", RegexOptions.CultureInvariant)]
    private static partial Regex FormatParameterRegex();
}
