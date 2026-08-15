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
        var replacement = store.Save(
            "封装夜班",
            replacementContent,
            first.Draft.FileFingerprint);

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
    public void Save_as_rejects_an_existing_target_without_overwriting_either_txt_file()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Save("source", "A1-1\n").Saved);
        Assert.True(store.Save("target", "B2-2\n").Saved);

        var result = store.SaveAs("target", "C3-3\n");
        var renameConflict = store.Rename(
            "source",
            "target",
            Fingerprint(store, "source"));

        Assert.False(result.Saved);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists,
            Assert.Single(result.Diagnostics).Code);
        Assert.False(renameConflict.Renamed);
        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists,
            Assert.Single(renameConflict.Diagnostics).Code);
        Assert.Equal("A1-1\n", store.Load("source").Content);
        Assert.Equal("B2-2\n", store.Load("target").Content);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Rename_moves_the_txt_and_updates_an_applied_marker_without_changing_its_snapshot()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        var before = store.Apply("东区", "A1-1\nA1-2\n").CurrentApplied;

        var result = store.Rename("东区", "生产东区", Fingerprint(store, "东区"));
        var restored = new WatchAreaFilterProfileStore(temporary.Path).LoadApplied();

        Assert.True(result.Renamed);
        Assert.True(result.Draft.IsValid);
        Assert.Equal("生产东区", result.Draft.ProfileName);
        Assert.Equal("A1-1\nA1-2\n", result.Draft.Content);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "东区.txt")));
        Assert.True(File.Exists(Path.Combine(temporary.Path, "生产东区.txt")));
        Assert.Equal("生产东区", result.CurrentApplied.ProfileName);
        Assert.Equal(before.MesAreas, result.CurrentApplied.MesAreas);
        Assert.Equal(before.AppliedAt, result.CurrentApplied.AppliedAt);
        Assert.Equal(result.CurrentApplied.ProfileName, restored.ProfileName);
        Assert.Equal(result.CurrentApplied.MesAreas, restored.MesAreas);
        Assert.Equal(result.CurrentApplied.AppliedAt, restored.AppliedAt);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Rename_updates_the_applied_marker_after_an_external_case_only_filename_change()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Apply("ActiveScope", "A1-1\n").Applied);
        var originalPath = Path.Combine(temporary.Path, "ActiveScope.txt");
        var transitPath = Path.Combine(temporary.Path, "case-change.tmp");
        var externallyRenamedPath = Path.Combine(temporary.Path, "ACTIVESCOPE.txt");
        File.Move(originalPath, transitPath);
        File.Move(transitPath, externallyRenamedPath);

        var result = store.Rename(
            "ACTIVESCOPE",
            "RenamedScope",
            Fingerprint(store, "ACTIVESCOPE"));
        var restored = new WatchAreaFilterProfileStore(temporary.Path).LoadApplied();

        Assert.True(result.Renamed);
        Assert.Equal("RenamedScope", result.CurrentApplied.ProfileName);
        Assert.Equal("RenamedScope", restored.ProfileName);
        Assert.Equal(["A1-1"], restored.MesAreas);
        Assert.False(File.Exists(externallyRenamedPath));
        Assert.True(File.Exists(Path.Combine(temporary.Path, "RenamedScope.txt")));
    }

    [Fact]
    public void Rename_rolls_the_txt_back_when_an_applied_marker_rewrite_fails()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Apply("source", "A1-1\n").Applied);
        var sourcePath = Path.Combine(temporary.Path, "source.txt");
        var sourceFingerprint = Fingerprint(store, "source");
        using var markerLock = new FileStream(
            store.ActiveMarkerPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        Assert.ThrowsAny<IOException>(() => store.Rename(
            "source",
            "renamed",
            sourceFingerprint));

        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "renamed.txt")));
        Assert.Equal("A1-1\n", store.Load("source").Content);
        Assert.Equal("source", store.LoadApplied().ProfileName);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Delete_removes_the_txt_and_explicitly_falls_back_from_an_applied_profile_to_all_areas()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Apply("东区", "A1-1\nA1-2\n").Applied);

        var result = store.Delete("东区", Fingerprint(store, "东区"));
        var restored = new WatchAreaFilterProfileStore(temporary.Path).LoadApplied();

        Assert.True(result.Deleted);
        Assert.Equal("东区", result.ProfileName);
        Assert.True(result.AppliedProfileWasDeleted);
        Assert.Empty(result.Diagnostics);
        Assert.True(result.CurrentApplied.IsAllAreas);
        Assert.NotNull(result.CurrentApplied.AppliedAt);
        Assert.Equal(result.CurrentApplied.ProfileName, restored.ProfileName);
        Assert.Equal(result.CurrentApplied.MesAreas, restored.MesAreas);
        Assert.Equal(result.CurrentApplied.AppliedAt, restored.AppliedAt);
        Assert.False(File.Exists(Path.Combine(temporary.Path, "东区.txt")));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void Delete_falls_back_when_an_externally_case_renamed_file_is_the_applied_profile()
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Apply("ActiveScope", "A1-1\n").Applied);
        var originalPath = Path.Combine(temporary.Path, "ActiveScope.txt");
        var transitPath = Path.Combine(temporary.Path, "case-change.tmp");
        var externallyRenamedPath = Path.Combine(temporary.Path, "ACTIVESCOPE.txt");
        File.Move(originalPath, transitPath);
        File.Move(transitPath, externallyRenamedPath);

        var result = store.Delete("ACTIVESCOPE", Fingerprint(store, "ACTIVESCOPE"));
        var restored = new WatchAreaFilterProfileStore(temporary.Path).LoadApplied();

        Assert.True(result.Deleted);
        Assert.True(result.AppliedProfileWasDeleted);
        Assert.True(result.CurrentApplied.IsAllAreas);
        Assert.True(restored.IsAllAreas);
        Assert.False(File.Exists(externallyRenamedPath));
    }

    [Fact]
    public void Delete_restores_the_txt_and_original_marker_when_tombstone_cleanup_fails()
    {
        using var temporary = new TemporaryDirectory();
        var setup = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(setup.Apply("AppliedScope", "A1-1\nB2-2\n").Applied);
        var fingerprint = Fingerprint(setup, "AppliedScope");
        var originalMarker = File.ReadAllText(setup.ActiveMarkerPath, Encoding.UTF8);
        var injectedDeleteReached = false;
        var failingStore = new WatchAreaFilterProfileStore(
            temporary.Path,
            deleteProfileFile: path =>
            {
                Assert.EndsWith(".delete.tmp", path, StringComparison.Ordinal);
                injectedDeleteReached = true;
                throw new IOException("deterministic tombstone cleanup failure");
            });

        var failure = Assert.Throws<IOException>(() => failingStore.Delete(
            "AppliedScope",
            fingerprint));

        Assert.True(injectedDeleteReached);
        Assert.Contains("deterministic", failure.Message, StringComparison.Ordinal);
        Assert.Equal("A1-1\nB2-2\n", setup.Load("AppliedScope").Content);
        Assert.Equal(originalMarker, File.ReadAllText(setup.ActiveMarkerPath, Encoding.UTF8));
        var restored = setup.LoadApplied();
        Assert.Equal("AppliedScope", restored.ProfileName);
        Assert.Equal(["A1-1", "B2-2"], restored.MesAreas);
        Assert.Empty(Directory.EnumerateFiles(
            temporary.Path,
            "*.delete.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData("rename", "malformed")]
    [InlineData("rename", "locked")]
    [InlineData("delete", "malformed")]
    [InlineData("delete", "locked")]
    public void Destructive_operations_fail_closed_when_the_applied_marker_cannot_be_read(
        string operation,
        string markerFailure)
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Apply("source", "A1-1\n").Applied);
        var sourceFingerprint = Fingerprint(store, "source");
        FileStream? markerLock = null;
        if (markerFailure == "malformed")
        {
            File.WriteAllText(store.ActiveMarkerPath, "not json", new UTF8Encoding(false));
        }
        else
        {
            markerLock = new FileStream(
                store.ActiveMarkerPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }

        try
        {
            IReadOnlyList<WatchAreaFilterProfileDiagnostic> diagnostics;
            if (operation == "rename")
            {
                var result = store.Rename("source", "renamed", sourceFingerprint);
                Assert.False(result.Renamed);
                diagnostics = result.Diagnostics;
            }
            else
            {
                var result = store.Delete("source", sourceFingerprint);
                Assert.False(result.Deleted);
                diagnostics = result.Diagnostics;
            }

            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(
                WatchAreaFilterProfileDiagnosticCodes.InvalidActiveMarker,
                diagnostic.Code);
            Assert.Contains("已取消", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("明确应用全部 AREA", diagnostic.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(temporary.Path, "source.txt")));
            Assert.False(File.Exists(Path.Combine(temporary.Path, "renamed.txt")));
        }
        finally
        {
            markerLock?.Dispose();
        }
    }

    [Theory]
    [InlineData("enumerate")]
    [InlineData("load")]
    [InlineData("save")]
    [InlineData("save-as")]
    [InlineData("apply")]
    [InlineData("all")]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task Every_profile_transaction_fails_busy_without_blocking_the_caller(
        string operation)
    {
        using var temporary = new TemporaryDirectory();
        var setup = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(setup.Apply("source", "A1-1\n").Applied);
        Assert.True(setup.Save("other", "B2-2\n").Saved);
        var concurrentStore = new WatchAreaFilterProfileStore(temporary.Path);
        var sourceFingerprint = Fingerprint(setup, "source");
        var lockPath = Path.Combine(temporary.Path, ".area-profiles.lock");
        using var transactionLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var operationTask = Task.Run(() => operation switch
        {
            "enumerate" => (object)concurrentStore.EnumerateProfiles(),
            "load" => concurrentStore.Load("source"),
            "save" => concurrentStore.Save("source", "C3-3\n"),
            "save-as" => concurrentStore.SaveAs("created", "C3-3\n"),
            "apply" => (object)concurrentStore.Apply("other"),
            "all" => concurrentStore.ApplyAllAreas(),
            "rename" => concurrentStore.Rename("source", "renamed", sourceFingerprint),
            "delete" => concurrentStore.Delete("source", sourceFingerprint),
            _ => throw new InvalidOperationException(operation),
        });
        var completed = await Task.WhenAny(operationTask, Task.Delay(500));
        if (!ReferenceEquals(completed, operationTask))
        {
            transactionLock.Dispose();
            await operationTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Fail("A busy AREA profile transaction must fail immediately instead of blocking the UI thread.");
        }

        var failure = await Assert.ThrowsAsync<IOException>(async () =>
            await operationTask.ConfigureAwait(false));
        Assert.Contains("另一进程", failure.Message, StringComparison.Ordinal);
        Assert.Contains("重试", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_read_and_destructive_writes_share_the_same_cross_instance_gate()
    {
        using var temporary = new TemporaryDirectory();
        var setup = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(setup.Apply("source", "A1-1\n").Applied);
        Assert.True(setup.Save("newer", "B2-2\n").Saved);
        var firstStore = new WatchAreaFilterProfileStore(temporary.Path);
        var secondStore = new WatchAreaFilterProfileStore(temporary.Path);
        var sourceFingerprint = Fingerprint(setup, "source");
        var lockPath = Path.Combine(temporary.Path, ".area-profiles.lock");
        using var transactionLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        var applyTask = Task.Run(() => firstStore.Apply("newer"));
        var renameTask = Task.Run(() => secondStore.Rename(
            "source",
            "renamed",
            sourceFingerprint));
        var deleteTask = Task.Run(() => secondStore.Delete("source", sourceFingerprint));

        foreach (var task in new Task[] { applyTask, renameTask, deleteTask })
        {
            var completed = await Task.WhenAny(task, Task.Delay(500));
            Assert.Same(task, completed);
            var failure = await Assert.ThrowsAsync<IOException>(async () =>
                await task.ConfigureAwait(false));
            Assert.Contains("另一进程", failure.Message, StringComparison.Ordinal);
        }

        transactionLock.Dispose();
        Assert.Equal("source", setup.LoadApplied().ProfileName);
        Assert.True(File.Exists(Path.Combine(temporary.Path, "source.txt")));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "renamed.txt")));
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
        var editOnly = store.Save(
            "封装班次",
            "B2-2",
            firstApply.Draft.FileFingerprint);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_and_save_and_apply_reject_a_stale_loaded_fingerprint(
        bool saveAndApply)
    {
        using var temporary = new TemporaryDirectory();
        var firstStore = new WatchAreaFilterProfileStore(temporary.Path);
        var secondStore = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(firstStore.Save("AppliedScope", "A1-1\n").Saved);
        Assert.True(firstStore.Save("EditableScope", "B2-2\n").Saved);
        Assert.True(firstStore.Apply("AppliedScope").Applied);
        var stale = firstStore.Load("EditableScope");
        var concurrent = secondStore.Load("EditableScope");
        Assert.NotNull(stale.FileFingerprint);
        Assert.Equal(stale.FileFingerprint, concurrent.FileFingerprint);
        Assert.True(secondStore.Save(
            "EditableScope",
            "C3-3\n",
            concurrent.FileFingerprint!).Saved);

        IReadOnlyList<WatchAreaFilterProfileDiagnostic> diagnostics;
        if (saveAndApply)
        {
            var result = firstStore.SaveAndApply(
                "EditableScope",
                "D4-4\n",
                stale.FileFingerprint!);
            Assert.False(result.Saved);
            Assert.False(result.Applied);
            diagnostics = result.Diagnostics;
        }
        else
        {
            var result = firstStore.Save(
                "EditableScope",
                "D4-4\n",
                stale.FileFingerprint!);
            Assert.False(result.Saved);
            diagnostics = result.Diagnostics;
        }

        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
            Assert.Single(diagnostics).Code);
        Assert.Equal("C3-3\n", firstStore.Load("EditableScope").Content);
        Assert.Equal("AppliedScope", firstStore.LoadApplied().ProfileName);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    public void Destructive_operations_reject_a_same_name_replacement_after_confirmation(
        string operation)
    {
        using var temporary = new TemporaryDirectory();
        var store = new WatchAreaFilterProfileStore(temporary.Path);
        Assert.True(store.Save("AppliedScope", "A1-1\n").Saved);
        Assert.True(store.Save("SourceScope", "B2-2\n").Saved);
        Assert.True(store.Apply("AppliedScope").Applied);
        var confirmed = store.Load("SourceScope");
        Assert.NotNull(confirmed.FileFingerprint);
        var sourcePath = Path.Combine(temporary.Path, "SourceScope.txt");
        var movedPath = Path.Combine(temporary.Path, "original-source.txt");
        File.Move(sourcePath, movedPath);
        File.WriteAllText(sourcePath, "C3-3\n", new UTF8Encoding(false));

        IReadOnlyList<WatchAreaFilterProfileDiagnostic> diagnostics;
        if (operation == "rename")
        {
            var result = store.Rename(
                "SourceScope",
                "RenamedScope",
                confirmed.FileFingerprint!);
            Assert.False(result.Renamed);
            diagnostics = result.Diagnostics;
        }
        else
        {
            var result = store.Delete("SourceScope", confirmed.FileFingerprint!);
            Assert.False(result.Deleted);
            diagnostics = result.Diagnostics;
        }

        Assert.Equal(
            WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
            Assert.Single(diagnostics).Code);
        Assert.Equal("C3-3\n", store.Load("SourceScope").Content);
        Assert.Equal("B2-2\n", File.ReadAllText(movedPath, Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(temporary.Path, "RenamedScope.txt")));
        Assert.Equal("AppliedScope", store.LoadApplied().ProfileName);
    }

    private static string Fingerprint(
        WatchAreaFilterProfileStore store,
        string profileName) => store.Load(profileName).FileFingerprint
        ?? throw new InvalidOperationException(
            $"{profileName}.txt must have a loaded fingerprint for this operation.");

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
