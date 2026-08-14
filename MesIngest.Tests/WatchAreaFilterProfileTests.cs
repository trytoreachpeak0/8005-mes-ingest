using System.Text;
using MesIngest.Watch;

namespace MesIngest.Tests;

public sealed class WatchAreaFilterProfileTests
{
    [Fact]
    public void Parser_reads_one_area_per_line_and_ignores_blank_and_whole_line_comments()
    {
        var content = """
            # 现场班次范围

              B2-2
            A1-1
               # 整行注释前允许空白
            """;

        var profile = WatchAreaFilterProfileParser.Parse("封装车间", content);

        Assert.True(profile.IsValid);
        Assert.Empty(profile.Diagnostics);
        Assert.Equal(["A1-1", "B2-2"], profile.MesAreas);
        Assert.Equal("封装车间", profile.ProfileName);
        Assert.Equal(content, profile.Content);
    }

    [Fact]
    public void Parser_reports_an_empty_effective_set_and_every_noncanonical_mes_area()
    {
        var empty = WatchAreaFilterProfileParser.Parse("空范围", "  \n# only a comment\n");
        var malformed = WatchAreaFilterProfileParser.Parse(
            "非法 AREA",
            "a1-1\nA01-1\nA1-01\nA1-1 # inline comments are values\n");

        var emptyDiagnostic = Assert.Single(empty.Diagnostics);
        Assert.Equal(WatchAreaFilterProfileDiagnosticCodes.EmptyAreaSet, emptyDiagnostic.Code);
        Assert.False(empty.IsValid);
        Assert.Empty(empty.MesAreas);

        Assert.False(malformed.IsValid);
        Assert.Empty(malformed.MesAreas);
        Assert.Equal(5, malformed.Diagnostics.Count);
        Assert.All(
            malformed.Diagnostics.Where(diagnostic => diagnostic.LineNumber is not null),
            diagnostic => Assert.Equal(
                WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea,
                diagnostic.Code));
        Assert.Equal(
            [1, 2, 3, 4],
            malformed.Diagnostics
                .Where(diagnostic => diagnostic.LineNumber is not null)
                .Select(item => item.LineNumber));
        Assert.Equal("A1-1 # inline comments are values", malformed.Diagnostics[3].Value);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.EmptyAreaSet,
            malformed.Diagnostics[4].Code);
    }

    [Fact]
    public void Parser_reports_duplicate_lines_and_more_than_one_hundred_distinct_areas()
    {
        var duplicate = WatchAreaFilterProfileParser.Parse(
            "重复项",
            "A1-1\nB2-2\nA1-1\n");
        var tooManyContent = string.Join(
            "\n",
            Enumerable.Range(1, 99)
                .Select(value => $"A1-{value}")
                .Concat(["B1-1", "B1-2"]));

        var tooMany = WatchAreaFilterProfileParser.Parse("超限", tooManyContent);

        var duplicateDiagnostic = Assert.Single(duplicate.Diagnostics);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.DuplicateMesArea,
            duplicateDiagnostic.Code);
        Assert.Equal(3, duplicateDiagnostic.LineNumber);
        Assert.Equal("A1-1", duplicateDiagnostic.Value);
        Assert.Equal(["A1-1", "B2-2"], duplicate.MesAreas);
        Assert.False(duplicate.IsValid);

        var countDiagnostic = Assert.Single(tooMany.Diagnostics);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.TooManyMesAreas,
            countDiagnostic.Code);
        Assert.Equal(101, tooMany.MesAreas.Count);
        Assert.False(tooMany.IsValid);
    }

    [Fact]
    public void Parser_accepts_exactly_one_hundred_distinct_areas()
    {
        var content = string.Join(
            "\n",
            Enumerable.Range(1, 99)
                .Select(value => $"A1-{value}")
                .Concat(["B1-1"]));

        var maximum = WatchAreaFilterProfileParser.Parse("最大合法范围", content);

        Assert.True(maximum.IsValid);
        Assert.Empty(maximum.Diagnostics);
        Assert.Equal(WatchAreaFilterProfileParser.MaximumAreaCount, maximum.MesAreas.Count);
    }

    [Fact]
    public void Parser_reports_empty_profile_names_as_structured_diagnostics()
    {
        var nullName = WatchAreaFilterProfileParser.Parse(null, "A1-1");
        var whitespaceName = WatchAreaFilterProfileParser.Parse("   ", "A1-1");

        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileNameRequired,
            Assert.Single(nullName.Diagnostics).Code);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileNameRequired,
            Assert.Single(whitespaceName.Diagnostics).Code);
        Assert.Equal(string.Empty, nullName.ProfileName);
        Assert.False(nullName.IsValid);
        Assert.False(whitespaceName.IsValid);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder\\escape")]
    [InlineData("C:\\escape")]
    [InlineData("CON")]
    [InlineData("..")]
    [InlineData("LPT1")]
    public void Parser_reports_dangerous_profile_names_as_structured_diagnostics(string name)
    {
        var profile = WatchAreaFilterProfileParser.Parse(name, "A1-1");

        var diagnostic = Assert.Single(profile.Diagnostics);
        Assert.Equal(WatchAreaFilterProfileDiagnosticCodes.UnsafeProfileName, diagnostic.Code);
        Assert.Equal(name, diagnostic.Value);
        Assert.False(profile.IsValid);
    }

    [Fact]
    public void Parser_trims_a_safe_profile_name()
    {
        var profile = WatchAreaFilterProfileParser.Parse("  封装夜班  ", "A1-1");

        Assert.True(profile.IsValid);
        Assert.Equal("封装夜班", profile.ProfileName);
    }

    [Fact]
    public void Store_defaults_to_local_app_data_and_accepts_an_injected_directory()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MesIngest.Watch",
            "area-filters");
        using var temporary = new TemporaryDirectory();

        var store = new WatchAreaFilterProfileStore(temporary.Path);

        Assert.Equal(expected, WatchAreaFilterProfileStore.DefaultDirectoryPath);
        Assert.Equal(Path.GetFullPath(temporary.Path), store.DirectoryPath);
    }

    [Fact]
    public void Store_enumerates_sorted_txt_profiles_and_loads_strict_utf8_content()
    {
        using var temporary = new TemporaryDirectory();
        var content = "# 中文注释\r\nB2-2\r\nA1-1\r\n";
        File.WriteAllBytes(
            Path.Combine(temporary.Path, "封装夜班.txt"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
        File.WriteAllText(Path.Combine(temporary.Path, "A 白班.txt"), "C3-3", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temporary.Path, "ignored.json"), "{}", Encoding.UTF8);
        File.WriteAllText(Path.Combine(temporary.Path, "orphan.tmp"), "partial", Encoding.UTF8);
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        var summaries = store.EnumerateProfiles();
        var loaded = store.Load("封装夜班");

        Assert.Equal(["A 白班", "封装夜班"], summaries.Select(item => item.ProfileName));
        Assert.Equal(["A 白班", "封装夜班"], summaries.Select(item => item.DisplaySummary));
        Assert.All(summaries, item => Assert.False(item.IsApplied));
        Assert.True(loaded.IsValid);
        Assert.Equal(content, loaded.Content);
        Assert.Equal(["A1-1", "B2-2"], loaded.MesAreas);
    }

    [Fact]
    public void Store_returns_structured_diagnostics_for_missing_or_non_utf8_profiles()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllBytes(
            Path.Combine(temporary.Path, "broken.txt"),
            [0xC3, 0x28]);
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        var missing = store.Load("missing");
        var broken = store.Load("broken");

        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound,
            Assert.Single(missing.Diagnostics).Code);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.InvalidUtf8,
            Assert.Single(broken.Diagnostics).Code);
        Assert.False(missing.IsValid);
        Assert.False(broken.IsValid);
        Assert.Empty(missing.MesAreas);
        Assert.Empty(broken.MesAreas);
    }

    [Fact]
    public void Save_atomically_creates_and_replaces_utf8_txt_without_temporary_residue()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        var firstContent = "# 夜班\nA1-1\n";
        var replacementContent = "# 夜班更新\nB2-2\n";

        var first = store.Save("封装夜班", firstContent);
        var replacement = store.Save("封装夜班", replacementContent);

        var savedPath = Path.Combine(temporary.Path, "封装夜班.txt");
        var bytes = File.ReadAllBytes(savedPath);
        Assert.True(first.Saved);
        Assert.True(replacement.Saved);
        Assert.Same(replacement.Draft.Diagnostics, replacement.Diagnostics);
        Assert.Same(replacement.Draft.MesAreas, replacement.MesAreas);
        Assert.Equal(["B2-2"], replacement.MesAreas);
        Assert.Equal(replacementContent, new UTF8Encoding(false, true).GetString(bytes));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Save_rejects_an_invalid_draft_without_creating_or_replacing_a_txt_file()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "profile.txt");
        var escapedName = $"escape-{Guid.NewGuid():N}";
        File.WriteAllText(path, "A1-1", new UTF8Encoding(false));
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        var malformed = store.Save("profile", "A01-1");
        var dangerous = store.Save($"../{escapedName}", "A1-1");

        Assert.False(malformed.Saved);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea,
            malformed.Diagnostics[0].Code);
        Assert.False(dangerous.Saved);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.UnsafeProfileName,
            dangerous.Diagnostics[0].Code);
        Assert.Equal("A1-1", File.ReadAllText(path, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(
            Directory.GetParent(temporary.Path)!.FullName,
            $"{escapedName}.txt")));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Save_cleans_its_temporary_file_when_the_atomic_directory_entry_switch_fails()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temporary.Path, "blocked.txt"));
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        Assert.ThrowsAny<IOException>(() => store.Save("blocked", "A1-1"));

        Assert.True(Directory.Exists(Path.Combine(temporary.Path, "blocked.txt")));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Save_does_not_change_the_persisted_applied_snapshot_until_explicit_apply()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Save("封装班次", "A1-1").Saved);

        var firstApply = store.Apply("封装班次");
        var editOnly = store.Save("封装班次", "B2-2");
        var afterEditAndRestart = new WatchAreaFilterProfileStore(temporary.Path).LoadApplied();

        Assert.True(firstApply.Applied);
        Assert.Same(firstApply.Draft.Diagnostics, firstApply.Diagnostics);
        Assert.Same(firstApply.Draft.MesAreas, firstApply.MesAreas);
        Assert.True(editOnly.Saved);
        Assert.Equal(["B2-2"], store.Load("封装班次").MesAreas);
        Assert.False(afterEditAndRestart.IsAllAreas);
        Assert.Equal("封装班次", afterEditAndRestart.ProfileName);
        Assert.Equal(["A1-1"], afterEditAndRestart.MesAreas);

        var secondApply = store.Apply("封装班次");
        Assert.True(secondApply.Applied);
        Assert.Equal(["B2-2"], secondApply.CurrentApplied.MesAreas);
        Assert.Equal(["B2-2"], new WatchAreaFilterProfileStore(temporary.Path).LoadApplied().MesAreas);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Apply_can_save_a_valid_draft_but_an_invalid_draft_cannot_replace_current_applied()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        var valid = store.Apply("白班", "A1-1\nB2-2");
        var invalid = store.Apply("夜班", "A01-1");

        Assert.True(valid.Applied);
        Assert.True(File.Exists(Path.Combine(temporary.Path, "白班.txt")));
        Assert.False(invalid.Applied);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea,
            invalid.Diagnostics[0].Code);
        Assert.Equal("白班", invalid.CurrentApplied.ProfileName);
        Assert.Equal(["A1-1", "B2-2"], invalid.CurrentApplied.MesAreas);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "夜班.txt")));
    }

    [Fact]
    public void Applied_profile_converts_to_the_window_display_context_and_marks_its_summary()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        var applied = store.Apply("封装车间", "B2-2\nA1-1").CurrentApplied;

        var context = applied.ToDisplayContext();
        var summaries = store.EnumerateProfiles();

        Assert.Equal("封装车间", context.ProfileName);
        Assert.Equal(["A1-1", "B2-2"], context.MesAreas);
        Assert.Equal("本机已应用", context.LocalState);
        Assert.Equal(applied.AppliedAt, context.LastUpdatedAt);
        Assert.Equal("封装车间 · 2 个 AREA", applied.DisplaySummary);
        var summary = Assert.Single(summaries);
        Assert.True(summary.IsApplied);
        Assert.Equal("封装车间 · 当前应用", summary.DisplaySummary);
    }

    [Fact]
    public void Missing_active_marker_is_a_normal_default_without_a_diagnostic()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        var state = store.LoadAppliedState();

        Assert.Null(state.Diagnostic);
        Assert.True(state.CurrentApplied.IsAllAreas);
        Assert.Null(state.CurrentApplied.ProfileName);
        Assert.Empty(state.CurrentApplied.MesAreas);
        Assert.Null(state.CurrentApplied.AppliedAt);
        Assert.Same(WatchAreaDisplayContext.AllAreas, state.CurrentApplied.ToDisplayContext());
        Assert.Same(WatchAppliedAreaFilterProfile.AllAreas, store.LoadApplied());
    }

    [Fact]
    public void Corrupt_active_marker_safely_falls_back_with_a_stable_diagnostic()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);

        File.WriteAllText(store.ActiveMarkerPath, "not json", new UTF8Encoding(false));
        var malformedJson = store.LoadAppliedState();
        File.WriteAllBytes(store.ActiveMarkerPath, [0xC3, 0x28]);
        var malformedUtf8 = store.LoadAppliedState();
        File.WriteAllText(
            store.ActiveMarkerPath,
            """
            {"version":1,"profileName":"../escape","mesAreas":["A1-1"],"appliedAt":"2026-08-14T12:00:00Z"}
            """,
            new UTF8Encoding(false));
        var unsafeDocument = store.LoadAppliedState();

        foreach (var state in new[] { malformedJson, malformedUtf8, unsafeDocument })
        {
            var diagnostic = Assert.IsType<WatchAreaFilterProfileDiagnostic>(state.Diagnostic);
            Assert.Equal(WatchAreaFilterProfileDiagnosticCodes.InvalidActiveMarker, diagnostic.Code);
            Assert.Equal("已应用 AREA 标记无法读取或内容无效，已回退为全部 AREA。", diagnostic.Message);

            var fallback = state.CurrentApplied;
            Assert.True(fallback.IsAllAreas);
            Assert.Null(fallback.ProfileName);
            Assert.Empty(fallback.MesAreas);
            Assert.Null(fallback.AppliedAt);
            Assert.Same(WatchAreaDisplayContext.AllAreas, fallback.ToDisplayContext());
        }

        Assert.True(store.LoadApplied().IsAllAreas);
    }

    [Fact]
    public void Apply_all_areas_is_explicitly_persisted_and_restored()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Apply("封装车间", "A1-1").Applied);

        var applied = store.ApplyAllAreas().CurrentApplied;
        var restored = new WatchAreaFilterProfileStore(temporary.Path).LoadApplied();
        var context = restored.ToDisplayContext();

        Assert.True(applied.IsAllAreas);
        Assert.True(restored.IsAllAreas);
        Assert.NotNull(restored.AppliedAt);
        Assert.Equal("全部 AREA", restored.DisplaySummary);
        Assert.Equal("全部 AREA", context.ProfileName);
        Assert.Equal("本机已应用", context.LocalState);
        Assert.Empty(context.MesAreas);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mes-watch-area-profile-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
