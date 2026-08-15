using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MesIngest.Watch;

internal sealed record WatchAreaFilterProfileDiagnostic(
    string Code,
    string Message,
    int? LineNumber = null,
    string? Value = null);

internal sealed record WatchAreaFilterProfile(
    string ProfileName,
    string Content,
    IReadOnlyList<string> MesAreas,
    IReadOnlyList<WatchAreaFilterProfileDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;

    public string? FileFingerprint { get; init; }
}

internal sealed record WatchAreaFilterProfileSummary(
    string ProfileName,
    DateTimeOffset LastModifiedAt,
    bool IsApplied)
{
    public string DisplaySummary => IsApplied
        ? $"{ProfileName} · 当前应用"
        : ProfileName;
}

internal sealed record WatchAreaFilterProfileSaveResult(
    bool Saved,
    WatchAreaFilterProfile Draft)
{
    public IReadOnlyList<WatchAreaFilterProfileDiagnostic> Diagnostics => Draft.Diagnostics;

    public IReadOnlyList<string> MesAreas => Draft.MesAreas;
}

internal sealed record WatchAreaFilterProfileRenameResult(
    bool Renamed,
    WatchAreaFilterProfile Draft,
    WatchAppliedAreaFilterProfile CurrentApplied)
{
    public IReadOnlyList<WatchAreaFilterProfileDiagnostic> Diagnostics => Draft.Diagnostics;
}

internal sealed record WatchAreaFilterProfileDeleteResult(
    bool Deleted,
    string ProfileName,
    bool AppliedProfileWasDeleted,
    WatchAppliedAreaFilterProfile CurrentApplied,
    IReadOnlyList<WatchAreaFilterProfileDiagnostic> Diagnostics);

internal sealed record WatchAppliedAreaFilterProfile(
    string? ProfileName,
    IReadOnlyList<string> MesAreas,
    DateTimeOffset? AppliedAt)
{
    public static WatchAppliedAreaFilterProfile AllAreas { get; } = new(null, [], null);

    public bool IsAllAreas => ProfileName is null;

    public string DisplaySummary => IsAllAreas
        ? "全部 AREA"
        : $"{ProfileName} · {MesAreas.Count} 个 AREA";

    public WatchAreaDisplayContext ToDisplayContext()
    {
        if (IsAllAreas && AppliedAt is null)
        {
            return WatchAreaDisplayContext.AllAreas;
        }

        return new WatchAreaDisplayContext(
                IsAllAreas ? "全部 AREA" : ProfileName!,
                MesAreas,
                "本机已应用",
                AppliedAt)
            .NormalizeAndValidate();
    }
}

internal sealed record WatchAreaFilterProfileApplyResult(
    bool Applied,
    WatchAreaFilterProfile Draft,
    WatchAppliedAreaFilterProfile CurrentApplied)
{
    public IReadOnlyList<WatchAreaFilterProfileDiagnostic> Diagnostics => Draft.Diagnostics;

    public IReadOnlyList<string> MesAreas => Draft.MesAreas;
}

internal sealed record WatchAreaFilterProfileSaveAndApplyResult(
    bool Saved,
    bool Applied,
    WatchAreaFilterProfile Draft,
    WatchAppliedAreaFilterProfile CurrentApplied,
    WatchAreaFilterProfileDiagnostic? ApplyDiagnostic)
{
    public IReadOnlyList<WatchAreaFilterProfileDiagnostic> Diagnostics =>
        ApplyDiagnostic is null
            ? Draft.Diagnostics
            : [.. Draft.Diagnostics, ApplyDiagnostic];
}

internal sealed record WatchAreaFilterAllAreasApplyResult(
    WatchAppliedAreaFilterProfile CurrentApplied);

internal sealed record WatchAreaFilterAppliedLoadResult(
    WatchAppliedAreaFilterProfile CurrentApplied,
    WatchAreaFilterProfileDiagnostic? Diagnostic);

internal static class WatchAreaFilterProfileDiagnosticCodes
{
    public const string InvalidActiveMarker = "INVALID_ACTIVE_MARKER";
    public const string DuplicateMesArea = "DUPLICATE_MES_AREA";
    public const string EmptyAreaSet = "EMPTY_AREA_SET";
    public const string InvalidMesArea = "INVALID_MES_AREA";
    public const string InvalidUtf8 = "INVALID_UTF8";
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string ProfileAlreadyExists = "PROFILE_ALREADY_EXISTS";
    public const string ProfileChangedOnDisk = "PROFILE_CHANGED_ON_DISK";
    public const string ProfileNameRequired = "PROFILE_NAME_REQUIRED";
    public const string ProfileNameUnchanged = "PROFILE_NAME_UNCHANGED";
    public const string TooManyMesAreas = "TOO_MANY_MES_AREAS";
    public const string UnsafeProfileName = "UNSAFE_PROFILE_NAME";
    public const string ActiveMarkerWriteFailed = "ACTIVE_MARKER_WRITE_FAILED";
}

internal static class WatchAreaFilterProfileParser
{
    public const int MaximumAreaCount = 100;
    public const int MaximumProfileNameLength = 80;

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly Regex MesAreaFormat = new(
        "^[A-Z][1-9][0-9]?-[1-9][0-9]?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static WatchAreaFilterProfile Parse(string? profileName, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var areas = new HashSet<string>(StringComparer.Ordinal);
        var diagnostics = new List<WatchAreaFilterProfileDiagnostic>();
        var normalizedProfileName = (profileName ?? string.Empty).Trim();
        if (normalizedProfileName.Length == 0)
        {
            diagnostics.Add(new WatchAreaFilterProfileDiagnostic(
                WatchAreaFilterProfileDiagnosticCodes.ProfileNameRequired,
                "必须填写 AREA 配置名称。"));
        }
        else if (!IsSafeProfileName(normalizedProfileName))
        {
            diagnostics.Add(new WatchAreaFilterProfileDiagnostic(
                WatchAreaFilterProfileDiagnosticCodes.UnsafeProfileName,
                "AREA 配置名称不能用作本地文件名。",
                Value: profileName));
        }

        var lineNumber = 0;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            if (!MesAreaFormat.IsMatch(value))
            {
                diagnostics.Add(new WatchAreaFilterProfileDiagnostic(
                    WatchAreaFilterProfileDiagnosticCodes.InvalidMesArea,
                    "AREA 必须匹配 ^[A-Z][1-9][0-9]?-[1-9][0-9]?$。",
                    lineNumber,
                    value));
                continue;
            }

            if (!areas.Add(value))
            {
                diagnostics.Add(new WatchAreaFilterProfileDiagnostic(
                    WatchAreaFilterProfileDiagnosticCodes.DuplicateMesArea,
                    $"AREA '{value}' 重复出现。",
                    lineNumber,
                    value));
            }
        }

        if (areas.Count > MaximumAreaCount)
        {
            diagnostics.Add(new WatchAreaFilterProfileDiagnostic(
                WatchAreaFilterProfileDiagnosticCodes.TooManyMesAreas,
                $"最多支持 {MaximumAreaCount} 个不同的 AREA。"));
        }

        if (areas.Count == 0)
        {
            diagnostics.Add(new WatchAreaFilterProfileDiagnostic(
                WatchAreaFilterProfileDiagnosticCodes.EmptyAreaSet,
                "AREA 配置必须至少包含一个有效 AREA。"));
        }

        return new WatchAreaFilterProfile(
            normalizedProfileName,
            content,
            areas.Order(StringComparer.Ordinal).ToArray(),
            diagnostics);
    }

    internal static bool IsSafeProfileName(string profileName)
    {
        if (profileName.Length == 0
            || profileName.Length > MaximumProfileNameLength
            || profileName is "." or ".."
            || Path.IsPathFullyQualified(profileName)
            || profileName.EndsWith(' ')
            || profileName.EndsWith('.'))
        {
            return false;
        }

        if (profileName.Any(character =>
                character < ' '
                || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                || Path.GetInvalidFileNameChars().Contains(character)))
        {
            return false;
        }

        var deviceStem = profileName.Split('.', 2)[0];
        return !ReservedWindowsNames.Contains(deviceStem);
    }
}

internal sealed class WatchAreaFilterProfileStore
{
    private const int ActiveMarkerVersion = 1;
    private const string ActiveMarkerFileName = ".active-profile";
    private const string MarkerTransactionLockFileName = ".area-profiles.lock";
    private const string ProfileExtension = ".txt";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions MarkerJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _deleteProfileFile;

    public WatchAreaFilterProfileStore(
        string? directoryPath = null,
        TimeProvider? timeProvider = null,
        Action<string>? deleteProfileFile = null)
    {
        var selectedPath = directoryPath ?? DefaultDirectoryPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        DirectoryPath = Path.GetFullPath(selectedPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deleteProfileFile = deleteProfileFile ?? File.Delete;
    }

    public static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesIngest.Watch",
        "area-filters");

    public string DirectoryPath { get; }

    public string ActiveMarkerPath => Path.Combine(DirectoryPath, ActiveMarkerFileName);

    public IReadOnlyList<WatchAreaFilterProfileSummary> EnumerateProfiles()
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return EnumerateProfilesNoLock();
    }

    private IReadOnlyList<WatchAreaFilterProfileSummary> EnumerateProfilesNoLock()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var appliedProfileName = LoadAppliedStateNoLock().CurrentApplied.ProfileName;
        return Directory
            .EnumerateFiles(DirectoryPath, $"*{ProfileExtension}", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                ProfileName = Path.GetFileNameWithoutExtension(path),
            })
            .Where(item => WatchAreaFilterProfileParser.IsSafeProfileName(item.ProfileName))
            .OrderBy(item => item.ProfileName, StringComparer.Ordinal)
            .Select(item => new WatchAreaFilterProfileSummary(
                item.ProfileName,
                File.GetLastWriteTimeUtc(item.Path),
                IsApplied: string.Equals(
                    item.ProfileName,
                    appliedProfileName,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public WatchAreaFilterProfile Load(string? profileName)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return LoadNoLock(profileName);
    }

    private WatchAreaFilterProfile LoadNoLock(string? profileName)
    {
        var nameCheck = WatchAreaFilterProfileParser.Parse(profileName, "A1-1");
        if (!nameCheck.IsValid)
        {
            return nameCheck with { Content = string.Empty, MesAreas = [] };
        }

        var path = GetProfilePath(nameCheck.ProfileName);
        ProfileFileSnapshot snapshot;
        try
        {
            snapshot = ReadProfileFileSnapshot(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return UnavailableProfile(
                nameCheck.ProfileName,
                WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound,
                "AREA 配置 TXT 文件不存在。");
        }

        try
        {
            var content = StrictUtf8.GetString(snapshot.Bytes);
            return WatchAreaFilterProfileParser.Parse(nameCheck.ProfileName, content) with
            {
                FileFingerprint = snapshot.Fingerprint,
            };
        }
        catch (DecoderFallbackException)
        {
            return UnavailableProfile(
                nameCheck.ProfileName,
                WatchAreaFilterProfileDiagnosticCodes.InvalidUtf8,
                "AREA 配置 TXT 文件不是有效的 UTF-8。") with
            {
                FileFingerprint = snapshot.Fingerprint,
            };
        }
    }

    public WatchAreaFilterProfileSaveResult Save(
        string? profileName,
        string content,
        string? expectedFingerprint = null)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return SaveNoLock(profileName, content, expectedFingerprint);
    }

    private WatchAreaFilterProfileSaveResult SaveNoLock(
        string? profileName,
        string content,
        string? expectedFingerprint)
    {
        var draft = WatchAreaFilterProfileParser.Parse(profileName, content);
        if (!draft.IsValid)
        {
            return new WatchAreaFilterProfileSaveResult(Saved: false, draft);
        }

        var path = GetProfilePath(draft.ProfileName);
        var fingerprintDiagnostic = ValidateSaveFingerprintNoLock(
            path,
            expectedFingerprint);
        if (fingerprintDiagnostic is not null)
        {
            return new WatchAreaFilterProfileSaveResult(
                Saved: false,
                AppendDiagnostic(
                    draft with { FileFingerprint = expectedFingerprint },
                    fingerprintDiagnostic.Code,
                    fingerprintDiagnostic.Message));
        }

        if (expectedFingerprint is null)
        {
            AtomicCreateText(path, draft.Content);
        }
        else
        {
            AtomicWriteText(path, draft.Content);
        }
        return new WatchAreaFilterProfileSaveResult(
            Saved: true,
            LoadNoLock(draft.ProfileName));
    }

    public WatchAreaFilterProfileSaveResult SaveAs(string? profileName, string content)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return SaveAsNoLock(profileName, content);
    }

    private WatchAreaFilterProfileSaveResult SaveAsNoLock(string? profileName, string content)
    {
        var draft = WatchAreaFilterProfileParser.Parse(profileName, content);
        if (!draft.IsValid)
        {
            return new WatchAreaFilterProfileSaveResult(Saved: false, draft);
        }

        var path = GetProfilePath(draft.ProfileName);
        if (File.Exists(path) || Directory.Exists(path))
        {
            return new WatchAreaFilterProfileSaveResult(
                Saved: false,
                AppendDiagnostic(
                    draft,
                    WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists,
                    $"AREA 配置 '{draft.ProfileName}.txt' 已存在；另存为不会覆盖现有文件。"));
        }

        AtomicCreateText(path, draft.Content);
        return new WatchAreaFilterProfileSaveResult(
            Saved: true,
            LoadNoLock(draft.ProfileName));
    }

    public WatchAreaFilterProfileRenameResult Rename(
        string? sourceProfileName,
        string? destinationProfileName,
        string expectedSourceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceFingerprint);
        using var transactionLock = AcquireProfileTransactionLock();
        var appliedState = LoadAppliedStateNoLock();
        var currentApplied = appliedState.CurrentApplied;
        var sourceNameCheck = WatchAreaFilterProfileParser.Parse(sourceProfileName, "A1-1");
        if (!sourceNameCheck.IsValid)
        {
            return new WatchAreaFilterProfileRenameResult(
                Renamed: false,
                sourceNameCheck with { Content = string.Empty, MesAreas = [] },
                currentApplied);
        }

        var sourcePath = GetProfilePath(sourceNameCheck.ProfileName);
        var sourceFingerprintDiagnostic = ValidateDestructiveFingerprintNoLock(
            sourcePath,
            expectedSourceFingerprint);
        if (sourceFingerprintDiagnostic is not null)
        {
            return new WatchAreaFilterProfileRenameResult(
                Renamed: false,
                AppendDiagnostic(
                    sourceNameCheck with { Content = string.Empty, MesAreas = [] },
                    sourceFingerprintDiagnostic.Code,
                    sourceFingerprintDiagnostic.Message),
                currentApplied);
        }

        var source = LoadNoLock(sourceNameCheck.ProfileName);
        var destinationNameCheck = WatchAreaFilterProfileParser.Parse(
            destinationProfileName,
            "A1-1");
        var draft = WatchAreaFilterProfileParser.Parse(
            destinationProfileName,
            source.Content);
        if (!destinationNameCheck.IsValid)
        {
            return new WatchAreaFilterProfileRenameResult(
                Renamed: false,
                draft,
                currentApplied);
        }

        if (appliedState.Diagnostic is not null)
        {
            return new WatchAreaFilterProfileRenameResult(
                Renamed: false,
                AppendDiagnostic(
                    draft,
                    WatchAreaFilterProfileDiagnosticCodes.InvalidActiveMarker,
                    "已应用 AREA 标记无法确认，重命名已取消；请先修复标记或明确应用全部 AREA。"),
                currentApplied);
        }

        if (string.Equals(
                sourceNameCheck.ProfileName,
                destinationNameCheck.ProfileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new WatchAreaFilterProfileRenameResult(
                Renamed: false,
                AppendDiagnostic(
                    draft,
                    WatchAreaFilterProfileDiagnosticCodes.ProfileNameUnchanged,
                    "新名称必须与当前 AREA 配置名称不同。"),
                currentApplied);
        }

        var destinationPath = GetProfilePath(destinationNameCheck.ProfileName);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            return new WatchAreaFilterProfileRenameResult(
                Renamed: false,
                AppendDiagnostic(
                    draft,
                    WatchAreaFilterProfileDiagnosticCodes.ProfileAlreadyExists,
                    $"AREA 配置 '{destinationNameCheck.ProfileName}.txt' 已存在；重命名不会覆盖现有文件。"),
                currentApplied);
        }

        File.Move(sourcePath, destinationPath);
        try
        {
            if (string.Equals(
                    currentApplied.ProfileName,
                    sourceNameCheck.ProfileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                currentApplied = currentApplied with
                {
                    ProfileName = destinationNameCheck.ProfileName,
                };
                WriteActiveMarker(new ActiveMarkerDocument(
                    ActiveMarkerVersion,
                    currentApplied.ProfileName,
                    currentApplied.MesAreas,
                    currentApplied.AppliedAt!.Value));
            }
        }
        catch
        {
            if (File.Exists(destinationPath) && !File.Exists(sourcePath))
            {
                File.Move(destinationPath, sourcePath);
            }

            throw;
        }

        return new WatchAreaFilterProfileRenameResult(
            Renamed: true,
            LoadNoLock(destinationNameCheck.ProfileName),
            currentApplied);
    }

    public WatchAreaFilterProfileDeleteResult Delete(
        string? profileName,
        string expectedSourceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSourceFingerprint);
        using var transactionLock = AcquireProfileTransactionLock();
        var appliedState = LoadAppliedStateNoLock();
        var currentApplied = appliedState.CurrentApplied;
        var appliedBeforeDelete = currentApplied;
        var nameCheck = WatchAreaFilterProfileParser.Parse(profileName, "A1-1");
        if (!nameCheck.IsValid)
        {
            return new WatchAreaFilterProfileDeleteResult(
                Deleted: false,
                nameCheck.ProfileName,
                AppliedProfileWasDeleted: false,
                currentApplied,
                nameCheck.Diagnostics);
        }

        if (appliedState.Diagnostic is not null)
        {
            return new WatchAreaFilterProfileDeleteResult(
                Deleted: false,
                nameCheck.ProfileName,
                AppliedProfileWasDeleted: false,
                currentApplied,
                [new WatchAreaFilterProfileDiagnostic(
                    WatchAreaFilterProfileDiagnosticCodes.InvalidActiveMarker,
                    "已应用 AREA 标记无法确认，删除已取消；请先修复标记或明确应用全部 AREA。")]);
        }

        var path = GetProfilePath(nameCheck.ProfileName);
        var sourceFingerprintDiagnostic = ValidateDestructiveFingerprintNoLock(
            path,
            expectedSourceFingerprint);
        if (sourceFingerprintDiagnostic is not null)
        {
            return new WatchAreaFilterProfileDeleteResult(
                Deleted: false,
                nameCheck.ProfileName,
                AppliedProfileWasDeleted: false,
                currentApplied,
                [sourceFingerprintDiagnostic]);
        }

        var tombstonePath = Path.Combine(
            DirectoryPath,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.delete.tmp");
        File.Move(path, tombstonePath);
        var appliedProfileWasDeleted = string.Equals(
            currentApplied.ProfileName,
            nameCheck.ProfileName,
            StringComparison.OrdinalIgnoreCase);
        var activeMarkerChanged = false;
        try
        {
            if (appliedProfileWasDeleted)
            {
                var appliedAt = _timeProvider.GetUtcNow();
                currentApplied = new WatchAppliedAreaFilterProfile(null, [], appliedAt);
                WriteActiveMarker(new ActiveMarkerDocument(
                    ActiveMarkerVersion,
                    ProfileName: null,
                    MesAreas: [],
                    AppliedAt: appliedAt));
                activeMarkerChanged = true;
            }

            _deleteProfileFile(tombstonePath);
        }
        catch
        {
            if (File.Exists(tombstonePath) && !File.Exists(path))
            {
                File.Move(tombstonePath, path);
            }

            if (activeMarkerChanged)
            {
                WriteActiveMarker(new ActiveMarkerDocument(
                    ActiveMarkerVersion,
                    appliedBeforeDelete.ProfileName,
                    appliedBeforeDelete.MesAreas,
                    appliedBeforeDelete.AppliedAt!.Value));
            }

            throw;
        }

        return new WatchAreaFilterProfileDeleteResult(
            Deleted: true,
            nameCheck.ProfileName,
            appliedProfileWasDeleted,
            currentApplied,
            []);
    }

    public WatchAreaFilterProfileApplyResult Apply(string? profileName)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        var draft = LoadNoLock(profileName);
        return ApplyDraftNoLock(draft);
    }

    public WatchAreaFilterProfileApplyResult Apply(
        string? profileName,
        string content,
        string? expectedFingerprint = null)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        var saved = SaveNoLock(profileName, content, expectedFingerprint);
        return saved.Saved
            ? ApplyDraftNoLock(saved.Draft)
            : new WatchAreaFilterProfileApplyResult(
                Applied: false,
                saved.Draft,
                LoadAppliedStateNoLock().CurrentApplied);
    }

    public WatchAreaFilterProfileSaveAndApplyResult SaveAndApply(
        string? profileName,
        string content,
        string expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        using var transactionLock = AcquireProfileTransactionLock();
        return CompleteSaveAndApplyNoLock(
            SaveNoLock(profileName, content, expectedFingerprint));
    }

    public WatchAreaFilterProfileSaveAndApplyResult SaveAsAndApply(
        string? profileName,
        string content)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return CompleteSaveAndApplyNoLock(SaveAsNoLock(profileName, content));
    }

    public WatchAreaFilterAllAreasApplyResult ApplyAllAreas()
    {
        using var transactionLock = AcquireProfileTransactionLock();
        var appliedAt = _timeProvider.GetUtcNow();
        WriteActiveMarker(new ActiveMarkerDocument(
            ActiveMarkerVersion,
            ProfileName: null,
            MesAreas: [],
            AppliedAt: appliedAt));
        return new WatchAreaFilterAllAreasApplyResult(
            new WatchAppliedAreaFilterProfile(null, [], appliedAt));
    }

    public WatchAppliedAreaFilterProfile LoadApplied()
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return LoadAppliedStateNoLock().CurrentApplied;
    }

    public WatchAreaFilterAppliedLoadResult LoadAppliedState()
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return LoadAppliedStateNoLock();
    }

    private WatchAreaFilterAppliedLoadResult LoadAppliedStateNoLock()
    {
        try
        {
            var content = File.ReadAllText(ActiveMarkerPath, StrictUtf8);
            var document = JsonSerializer.Deserialize<ActiveMarkerDocument>(
                content,
                MarkerJsonOptions);
            var applied = document?.ToAppliedProfile();
            return applied is not null
                ? new WatchAreaFilterAppliedLoadResult(applied, Diagnostic: null)
                : InvalidActiveMarkerFallback();
        }
        catch (Exception ex) when (ex is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return new WatchAreaFilterAppliedLoadResult(
                WatchAppliedAreaFilterProfile.AllAreas,
                Diagnostic: null);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or JsonException
            or NotSupportedException)
        {
            return InvalidActiveMarkerFallback();
        }
    }

    private static WatchAreaFilterAppliedLoadResult InvalidActiveMarkerFallback() => new(
        WatchAppliedAreaFilterProfile.AllAreas,
        new WatchAreaFilterProfileDiagnostic(
            WatchAreaFilterProfileDiagnosticCodes.InvalidActiveMarker,
            "已应用 AREA 标记无法读取或内容无效，已回退为全部 AREA。"));

    private WatchAreaFilterProfileApplyResult ApplyDraftNoLock(WatchAreaFilterProfile draft)
    {
        if (!draft.IsValid)
        {
            return new WatchAreaFilterProfileApplyResult(
                Applied: false,
                draft,
                LoadAppliedStateNoLock().CurrentApplied);
        }

        var appliedAt = _timeProvider.GetUtcNow();
        var current = new WatchAppliedAreaFilterProfile(
            draft.ProfileName,
            draft.MesAreas.ToArray(),
            appliedAt);
        WriteActiveMarker(new ActiveMarkerDocument(
            ActiveMarkerVersion,
            current.ProfileName,
            current.MesAreas,
            AppliedAt: appliedAt));
        return new WatchAreaFilterProfileApplyResult(Applied: true, draft, current);
    }

    private WatchAreaFilterProfileSaveAndApplyResult CompleteSaveAndApplyNoLock(
        WatchAreaFilterProfileSaveResult saved)
    {
        var previousApplied = LoadAppliedStateNoLock().CurrentApplied;
        if (!saved.Saved)
        {
            return new WatchAreaFilterProfileSaveAndApplyResult(
                Saved: false,
                Applied: false,
                saved.Draft,
                previousApplied,
                ApplyDiagnostic: null);
        }

        try
        {
            var applied = ApplyDraftNoLock(saved.Draft);
            return new WatchAreaFilterProfileSaveAndApplyResult(
                Saved: true,
                Applied: applied.Applied,
                applied.Draft,
                applied.CurrentApplied,
                ApplyDiagnostic: null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return new WatchAreaFilterProfileSaveAndApplyResult(
                Saved: true,
                Applied: false,
                saved.Draft,
                previousApplied,
                new WatchAreaFilterProfileDiagnostic(
                    WatchAreaFilterProfileDiagnosticCodes.ActiveMarkerWriteFailed,
                    $"{saved.Draft.ProfileName}.txt 已保存，但范围未应用；请处理本机范围标记后重试。{exception.Message}"));
        }
    }

    private void WriteActiveMarker(ActiveMarkerDocument marker)
    {
        var content = JsonSerializer.Serialize(marker, MarkerJsonOptions);
        AtomicWriteText(ActiveMarkerPath, content);
    }

    private FileStream AcquireProfileTransactionLock()
    {
        Directory.CreateDirectory(DirectoryPath);
        var lockPath = Path.Combine(DirectoryPath, MarkerTransactionLockFileName);
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new IOException(
                "AREA 配置正由另一进程修改；请稍后重试。",
                exception);
        }
    }

    private static WatchAreaFilterProfileDiagnostic? ValidateSaveFingerprintNoLock(
        string path,
        string? expectedFingerprint)
    {
        if (Directory.Exists(path))
        {
            return expectedFingerprint is null
                ? null
                : ProfileChangedOnDiskDiagnostic(
                    "AREA 配置 TXT 路径已被其他文件系统对象替代；已保留当前草稿，请重新加载后再试。");
        }

        ProfileFileSnapshot current;
        try
        {
            current = ReadProfileFileSnapshot(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return expectedFingerprint is null
                ? null
                : ProfileChangedOnDiskDiagnostic(
                    "AREA 配置 TXT 已在磁盘移动或删除；已保留当前草稿，请重新加载后再试。");
        }

        return expectedFingerprint is not null
            && string.Equals(
                current.Fingerprint,
                expectedFingerprint,
                StringComparison.Ordinal)
                ? null
                : ProfileChangedOnDiskDiagnostic(
                    "AREA 配置 TXT 已在磁盘更改；已保留当前草稿和磁盘版本，请重新加载后再试。");
    }

    private static WatchAreaFilterProfileDiagnostic? ValidateDestructiveFingerprintNoLock(
        string path,
        string expectedFingerprint)
    {
        try
        {
            var current = ReadProfileFileSnapshot(path);
            return string.Equals(
                current.Fingerprint,
                expectedFingerprint,
                StringComparison.Ordinal)
                    ? null
                    : ProfileChangedOnDiskDiagnostic(
                        "AREA 配置 TXT 在确认后已被替换或更改；操作已取消，请重新选择并确认。");
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return ProfileChangedOnDiskDiagnostic(
                "AREA 配置 TXT 在确认后已被移动或删除；操作已取消，请重新选择并确认。");
        }
    }

    private static WatchAreaFilterProfileDiagnostic ProfileChangedOnDiskDiagnostic(
        string message) => new(
        WatchAreaFilterProfileDiagnosticCodes.ProfileChangedOnDisk,
        message);

    private static ProfileFileSnapshot ReadProfileFileSnapshot(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > int.MaxValue)
        {
            throw new IOException("AREA 配置 TXT 文件过大，无法安全读取。");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var creationTimeTicks = File.GetCreationTimeUtc(path).Ticks;
        var lastWriteTimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        var fingerprint = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"v1:{creationTimeTicks:X16}:{lastWriteTimeTicks:X16}:{bytes.Length:X16}:{contentHash}");
        return new ProfileFileSnapshot(bytes, fingerprint);
    }

    private static WatchAreaFilterProfile UnavailableProfile(
        string profileName,
        string code,
        string message) => new(
        profileName,
        string.Empty,
        [],
        [new WatchAreaFilterProfileDiagnostic(code, message)]);

    private static WatchAreaFilterProfile AppendDiagnostic(
        WatchAreaFilterProfile profile,
        string code,
        string message) => profile with
        {
            Diagnostics =
            [
                .. profile.Diagnostics,
                new WatchAreaFilterProfileDiagnostic(code, message),
            ],
        };

    private string GetProfilePath(string profileName) =>
        Path.Combine(DirectoryPath, $"{profileName}{ProfileExtension}");

    private sealed record ProfileFileSnapshot(byte[] Bytes, string Fingerprint);

    private static void AtomicWriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("AREA 配置路径缺少父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteTemporaryText(temporaryPath, content);

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void AtomicCreateText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("AREA 配置路径缺少父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteTemporaryText(temporaryPath, content);
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void WriteTemporaryText(string temporaryPath, string content)
    {
        using var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        using (var writer = new StreamWriter(
                   stream,
                   StrictUtf8,
                   bufferSize: 4096,
                   leaveOpen: true))
        {
            writer.Write(content);
            writer.Flush();
        }

        stream.Flush(flushToDisk: true);
    }

    private sealed record ActiveMarkerDocument(
        int Version,
        string? ProfileName,
        IReadOnlyList<string>? MesAreas,
        DateTimeOffset AppliedAt)
    {
        public WatchAppliedAreaFilterProfile? ToAppliedProfile()
        {
            if (Version != ActiveMarkerVersion
                || AppliedAt == default
                || MesAreas is null)
            {
                return null;
            }

            if (ProfileName is null)
            {
                return MesAreas.Count == 0
                    ? new WatchAppliedAreaFilterProfile(null, [], AppliedAt)
                    : null;
            }

            if (MesAreas.Any(string.IsNullOrWhiteSpace))
            {
                return null;
            }

            var parsed = WatchAreaFilterProfileParser.Parse(
                ProfileName,
                string.Join("\n", MesAreas));
            return parsed.IsValid
                && string.Equals(ProfileName, parsed.ProfileName, StringComparison.Ordinal)
                && MesAreas.SequenceEqual(parsed.MesAreas, StringComparer.Ordinal)
                    ? new WatchAppliedAreaFilterProfile(
                        parsed.ProfileName,
                        parsed.MesAreas,
                        AppliedAt)
                    : null;
        }
    }
}
