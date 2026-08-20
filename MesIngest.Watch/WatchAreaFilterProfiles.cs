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

internal enum WatchAreaFilterProfileAvailability
{
    Present,
    AppliedSnapshotWithoutFile,
}

internal sealed record WatchAreaFilterProfileSummary(
    string ProfileName,
    DateTimeOffset? FileLastModifiedAt,
    bool IsApplied,
    WatchAreaFilterProfileAvailability Availability)
{
    public bool IsMissing =>
        Availability == WatchAreaFilterProfileAvailability.AppliedSnapshotWithoutFile;

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

internal sealed class WatchAreaFilterProfileStore : IDisposable
{
    private const int ActiveMarkerVersion = 1;
    private const string ActiveMarkerFileName = ".active-profile";
    private const string MarkerTransactionLockFileName = ".area-profiles.lock";
    internal const string ProfileExtension = ".txt";

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
    private readonly IWatchAreaProfileDirectoryEventSource? _configuredDirectoryEventSource;
    private readonly object _directoryWatchGate = new();
    private readonly SortedSet<string> _pendingChangedProfileNames =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchAreaProfileRename> _pendingProfileRenames = [];
    private readonly Dictionary<string, ITimer> _pendingDeleteConfirmations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OwnProfileWrite> _ownProfileWrites =
        new(StringComparer.OrdinalIgnoreCase);
    private IWatchAreaProfileDirectoryEventSource? _directoryEventSource;
    private ITimer? _directoryChangeDebounceTimer;
    private ITimer? _directoryWatchHealthTimer;
    private bool _directoryInitialized;
    private DirectoryWatchLifecycle _directoryWatchLifecycle;
    private bool _directoryWatchNeedsFullRescan;
    private bool _disposed;

    public WatchAreaFilterProfileStore(
        string? directoryPath = null,
        TimeProvider? timeProvider = null,
        Action<string>? deleteProfileFile = null,
        IWatchAreaProfileDirectoryEventSource? directoryEventSource = null)
    {
        var selectedPath = directoryPath ?? DefaultDirectoryPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        DirectoryPath = Path.GetFullPath(selectedPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deleteProfileFile = deleteProfileFile ?? File.Delete;
        _configuredDirectoryEventSource = directoryEventSource;
    }

    /// <summary>
    /// Raw events observed within this window of the first pending one are
    /// coalesced into a single interpreted change. The window is anchored to
    /// that first event, so a continuous stream of writes still gets announced
    /// once per window instead of postponing the change indefinitely.
    /// </summary>
    public static TimeSpan DirectoryChangeDebounceWindow { get; } =
        TimeSpan.FromMilliseconds(250);

    public static TimeSpan DeleteConfirmationWindow { get; } =
        TimeSpan.FromMilliseconds(500);

    public static TimeSpan DirectoryWatchHealthCheckInterval { get; } =
        TimeSpan.FromSeconds(1);

    public static TimeSpan DirectoryWatchRecoveryInterval { get; } =
        DirectoryWatchHealthCheckInterval;

    /// <summary>
    /// Idle time between the last keystroke and the automatic write. Matches
    /// the `files.autoSaveDelay` default of the editors users compare this
    /// page against, so the buffer is never dirty for more than about a second.
    /// </summary>
    public static TimeSpan EditorAutoSaveDelay { get; } = TimeSpan.FromSeconds(1);

    public static TimeSpan OwnWriteSuppressionWindow { get; } =
        TimeSpan.FromSeconds(2);

    public static IReadOnlyList<TimeSpan> ExternalReadRetryDelays { get; } =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
    ];

    /// <summary>
    /// Raised once a debounce window closes, on whichever thread the event
    /// source uses. Subscribers that touch UI must marshal to their own thread.
    /// </summary>
    public event EventHandler<WatchAreaProfileDirectoryChange>? DirectoryChanged;

    public event EventHandler<WatchAreaProfileDirectoryWatchStateChange>?
        DirectoryWatchStateChanged;

    public static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesIngest.Watch",
        "area-filters");

    public string DirectoryPath { get; }

    public string ActiveMarkerPath => Path.Combine(DirectoryPath, ActiveMarkerFileName);

    public void EnsureDirectoryExists()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_directoryWatchGate)
        {
            if (_directoryInitialized)
            {
                return;
            }
        }

        Directory.CreateDirectory(DirectoryPath);
        lock (_directoryWatchGate)
        {
            _directoryInitialized = true;
        }
    }

    public IReadOnlyList<WatchAreaFilterProfileSummary> EnumerateProfiles()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        using var transactionLock = AcquireProfileTransactionLock();
        return EnumerateProfilesNoLock();
    }

    private IReadOnlyList<WatchAreaFilterProfileSummary> EnumerateProfilesNoLock()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var applied = LoadAppliedStateNoLock().CurrentApplied;
        var summaries = Directory
            .EnumerateFiles(DirectoryPath, $"*{ProfileExtension}", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                ProfileName = Path.GetFileNameWithoutExtension(path),
            })
            .Where(item => IsProfileFileName(Path.GetFileName(item.Path))
                && !HasCompanionFileAttributes(item.Path))
            .OrderBy(item => item.ProfileName, StringComparer.Ordinal)
            .Select(item => new WatchAreaFilterProfileSummary(
                item.ProfileName,
                File.GetLastWriteTimeUtc(item.Path),
                IsApplied: string.Equals(
                    item.ProfileName,
                    applied.ProfileName,
                    StringComparison.OrdinalIgnoreCase),
                WatchAreaFilterProfileAvailability.Present))
            .ToList();
        if (applied.ProfileName is { } appliedProfileName
            && !summaries.Any(summary => string.Equals(
                summary.ProfileName,
                appliedProfileName,
                StringComparison.OrdinalIgnoreCase)))
        {
            summaries.Add(new WatchAreaFilterProfileSummary(
                appliedProfileName,
                FileLastModifiedAt: null,
                IsApplied: true,
                WatchAreaFilterProfileAvailability.AppliedSnapshotWithoutFile));
        }

        return summaries
            .OrderBy(summary => summary.ProfileName, StringComparer.Ordinal)
            .ToArray();
    }

    public WatchAreaFilterProfile Load(string? profileName)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return LoadNoLock(profileName);
    }

    public Task<WatchAreaFilterProfile> LoadAfterExternalChangeAsync(
        string profileName,
        CancellationToken cancellationToken = default) =>
        new ExternalProfileLoadRetry(this, profileName, cancellationToken).Start();

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
        return CompleteSuccessfulSaveNoLock(draft.ProfileName);
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
        return CompleteSuccessfulSaveNoLock(draft.ProfileName);
    }

    /// <summary>
    /// Writes the editor buffer without demanding valid AREA content. Automatic
    /// saving replaced the explicit save button, so refusing an unfinished
    /// buffer would silently drop what the user typed; only a name that cannot
    /// become a file still blocks the write. Whether the content may become the
    /// displayed range stays with <see cref="Apply(string)"/>.
    /// </summary>
    public WatchAreaFilterProfileSaveResult AutoSave(
        string? profileName,
        string content,
        string? expectedFingerprint = null)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        return AutoSaveNoLock(profileName, content, expectedFingerprint);
    }

    /// <summary>
    /// Resolves a write conflict in favour of the editor buffer: the file's
    /// current fingerprint is read and written against inside one transaction,
    /// so a third writer cannot slip in between the two and be overwritten
    /// unnoticed. This is the same optimistic concurrency as
    /// <see cref="AutoSave"/>, not a second baseline mechanism.
    /// </summary>
    public WatchAreaFilterProfileSaveResult OverwriteWithLocalEdit(
        string? profileName,
        string content)
    {
        using var transactionLock = AcquireProfileTransactionLock();
        var nameCheck = WatchAreaFilterProfileParser.Parse(profileName, content);
        if (HasUnusableProfileName(nameCheck))
        {
            return new WatchAreaFilterProfileSaveResult(Saved: false, nameCheck);
        }

        var path = GetProfilePath(nameCheck.ProfileName);
        string? currentFingerprint = null;
        if (File.Exists(path))
        {
            currentFingerprint = ReadProfileFileSnapshot(path).Fingerprint;
        }

        return AutoSaveNoLock(profileName, content, currentFingerprint);
    }

    private WatchAreaFilterProfileSaveResult AutoSaveNoLock(
        string? profileName,
        string content,
        string? expectedFingerprint)
    {
        var draft = WatchAreaFilterProfileParser.Parse(profileName, content);
        if (HasUnusableProfileName(draft))
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

        return CompleteSuccessfulSaveNoLock(draft.ProfileName);
    }

    /// <summary>
    /// Diagnostics about the name the draft would be filed under, as opposed to
    /// diagnostics about its AREA content. A draft carrying only these can
    /// still be written once a name is supplied — which is what "save as" does.
    /// </summary>
    internal static bool HasUnusableProfileName(WatchAreaFilterProfile draft) =>
        draft.Diagnostics.Any(IsProfileNameDiagnostic);

    internal static bool HasContentDiagnostics(WatchAreaFilterProfile draft) =>
        draft.Diagnostics.Any(diagnostic => !IsProfileNameDiagnostic(diagnostic));

    private static bool IsProfileNameDiagnostic(
        WatchAreaFilterProfileDiagnostic diagnostic) => diagnostic.Code
        is WatchAreaFilterProfileDiagnosticCodes.ProfileNameRequired
        or WatchAreaFilterProfileDiagnosticCodes.UnsafeProfileName;

    private WatchAreaFilterProfileSaveResult CompleteSuccessfulSaveNoLock(
        string profileName)
    {
        var savedDraft = LoadNoLock(profileName);
        RememberOwnProfileWrite(savedDraft);
        return new WatchAreaFilterProfileSaveResult(Saved: true, savedDraft);
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
        try
        {
            _deleteProfileFile(tombstonePath);
        }
        catch
        {
            if (File.Exists(tombstonePath) && !File.Exists(path))
            {
                File.Move(tombstonePath, path);
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
        if (!Directory.Exists(DirectoryPath))
        {
            return WatchAppliedAreaFilterProfile.AllAreas;
        }

        using var transactionLock = AcquireProfileTransactionLock();
        return LoadAppliedStateNoLock().CurrentApplied;
    }

    public WatchAreaFilterAppliedLoadResult LoadAppliedState()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return new WatchAreaFilterAppliedLoadResult(
                WatchAppliedAreaFilterProfile.AllAreas,
                Diagnostic: null);
        }

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

    /// <summary>
    /// Starts observing the profile directory. Idempotent; the store owns the
    /// event source lifetime, so callers only have to dispose the store.
    /// </summary>
    public void StartWatchingDirectory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IWatchAreaProfileDirectoryEventSource source;
        lock (_directoryWatchGate)
        {
            if (_directoryWatchLifecycle is not DirectoryWatchLifecycle.Stopped)
            {
                return;
            }

            _directoryWatchLifecycle = DirectoryWatchLifecycle.Starting;
            source = GetOrCreateDirectoryEventSourceNoLock();
            EnsureDirectoryWatchHealthTimerNoLock();
        }

        try
        {
            EnsureDirectoryExists();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            EnterDirectoryWatchDegraded(
                $"AREA 配置目录暂时无法创建或访问：{exception.Message}");
            return;
        }

        TryStartDirectoryEventSource(source, failureRescanAlreadyEmitted: false);
    }

    public void StopWatchingDirectory()
    {
        IWatchAreaProfileDirectoryEventSource? source;
        ITimer? debounceTimer;
        ITimer[] deleteConfirmationTimers;
        lock (_directoryWatchGate)
        {
            if (_disposed
                || _directoryWatchLifecycle is DirectoryWatchLifecycle.Stopped)
            {
                return;
            }

            _directoryWatchLifecycle = DirectoryWatchLifecycle.Stopped;
            _directoryWatchNeedsFullRescan = true;
            source = _directoryEventSource;
            debounceTimer = _directoryChangeDebounceTimer;
            _directoryChangeDebounceTimer = null;
            _directoryWatchHealthTimer?.Change(
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _pendingChangedProfileNames.Clear();
            _pendingProfileRenames.Clear();
            deleteConfirmationTimers = [.. _pendingDeleteConfirmations.Values];
            _pendingDeleteConfirmations.Clear();
        }

        source?.Stop();
        debounceTimer?.Dispose();
        foreach (var timer in deleteConfirmationTimers)
        {
            timer.Dispose();
        }

        RaiseDirectoryWatchStateChanged(
            WatchAreaProfileDirectoryWatchStatus.Stopped,
            "AREA 配置目录监视已暂停。");
    }

    public void Dispose()
    {
        IWatchAreaProfileDirectoryEventSource? source;
        ITimer? debounceTimer;
        ITimer? healthTimer;
        ITimer[] deleteConfirmationTimers;
        lock (_directoryWatchGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _directoryWatchLifecycle = DirectoryWatchLifecycle.Stopped;
            source = _directoryEventSource;
            debounceTimer = _directoryChangeDebounceTimer;
            healthTimer = _directoryWatchHealthTimer;
            _directoryEventSource = null;
            _directoryChangeDebounceTimer = null;
            _directoryWatchHealthTimer = null;
            _pendingChangedProfileNames.Clear();
            _pendingProfileRenames.Clear();
            deleteConfirmationTimers = [.. _pendingDeleteConfirmations.Values];
            _pendingDeleteConfirmations.Clear();
            _ownProfileWrites.Clear();
        }

        DirectoryChanged = null;
        DirectoryWatchStateChanged = null;
        if (source is not null)
        {
            source.Raised -= OnDirectoryEventRaised;
            source.Failed -= OnDirectoryEventSourceFailed;
            source.Dispose();
        }

        debounceTimer?.Dispose();
        healthTimer?.Dispose();
        foreach (var timer in deleteConfirmationTimers)
        {
            timer.Dispose();
        }
    }

    private void OnDirectoryEventRaised(
        object? sender,
        WatchAreaProfileDirectoryEvent directoryEvent)
    {
        lock (_directoryWatchGate)
        {
            if (_disposed
                || _directoryWatchLifecycle is not DirectoryWatchLifecycle.Watching)
            {
                return;
            }
        }

        if (directoryEvent.Kind is WatchAreaProfileDirectoryEventKind.Renamed)
        {
            InterpretRename(directoryEvent);
            return;
        }

        if (!TryGetProfileName(directoryEvent.FileName, out var profileName))
        {
            return;
        }

        if (directoryEvent.Kind is WatchAreaProfileDirectoryEventKind.Deleted)
        {
            ScheduleDeleteConfirmation(profileName);
            return;
        }

        CancelDeleteConfirmation(profileName);
        QueueDirectoryChange([profileName]);
    }

    private void InterpretRename(WatchAreaProfileDirectoryEvent directoryEvent)
    {
        var hasNewProfileName = TryGetProfileName(
            directoryEvent.FileName,
            out var profileName);
        var hasPreviousProfileName = TryGetProfileName(
            directoryEvent.PreviousFileName,
            out var previousProfileName);
        if (hasNewProfileName && hasPreviousProfileName)
        {
            CancelDeleteConfirmation(previousProfileName);
            CancelDeleteConfirmation(profileName);
            QueueDirectoryChange(
                [previousProfileName, profileName],
                new WatchAreaProfileRename(previousProfileName, profileName));
            return;
        }

        if (hasNewProfileName)
        {
            CancelDeleteConfirmation(profileName);
            QueueDirectoryChange([profileName]);
        }
        else if (hasPreviousProfileName)
        {
            ScheduleDeleteConfirmation(previousProfileName);
        }
    }

    private void QueueDirectoryChange(
        IReadOnlyList<string> changedProfileNames,
        WatchAreaProfileRename? rename = null)
    {
        lock (_directoryWatchGate)
        {
            if (_disposed || _directoryEventSource is null)
            {
                return;
            }

            var windowAlreadyOpen = _pendingChangedProfileNames.Count != 0;
            foreach (var profileName in changedProfileNames)
            {
                _pendingChangedProfileNames.Add(profileName);
            }
            if (rename is not null && !_pendingProfileRenames.Contains(rename))
            {
                _pendingProfileRenames.Add(rename);
            }

            if (windowAlreadyOpen)
            {
                return;
            }

            _directoryChangeDebounceTimer ??= _timeProvider.CreateTimer(
                _ => RaisePendingDirectoryChange(),
                state: null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _directoryChangeDebounceTimer.Change(
                DirectoryChangeDebounceWindow,
                Timeout.InfiniteTimeSpan);
        }
    }

    private bool TryGetProfileName(string? fileName, out string profileName)
    {
        if (fileName is null)
        {
            profileName = string.Empty;
            return false;
        }

        var name = Path.GetFileName(fileName);
        if (!IsProfileFileName(name)
            || HasCompanionFileAttributes(Path.Combine(DirectoryPath, name)))
        {
            profileName = string.Empty;
            return false;
        }

        profileName = Path.GetFileNameWithoutExtension(name);
        return true;
    }

    private void ScheduleDeleteConfirmation(string profileName)
    {
        lock (_directoryWatchGate)
        {
            if (_disposed || _directoryEventSource is null
                || _pendingDeleteConfirmations.ContainsKey(profileName))
            {
                return;
            }

            // A watcher can report Changed immediately before Deleted for the
            // same editor save. Once deletion is observed, its confirmation
            // window owns this identity; an older debounced change must not
            // refresh the list while the file is temporarily absent.
            _pendingChangedProfileNames.Remove(profileName);
            _pendingDeleteConfirmations[profileName] = _timeProvider.CreateTimer(
                _ => ConfirmDelete(profileName),
                state: null,
                DeleteConfirmationWindow,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void CancelDeleteConfirmation(string profileName)
    {
        ITimer? timer = null;
        lock (_directoryWatchGate)
        {
            if (_pendingDeleteConfirmations.Remove(profileName, out var pendingTimer))
            {
                timer = pendingTimer;
            }
        }

        timer?.Dispose();
    }

    private void ConfirmDelete(string profileName)
    {
        ITimer? timer;
        lock (_directoryWatchGate)
        {
            if (_disposed
                || !_pendingDeleteConfirmations.Remove(profileName, out timer))
            {
                return;
            }
        }

        timer.Dispose();
        var deletedProfileNames = File.Exists(GetProfilePath(profileName))
            ? Array.Empty<string>()
            : [profileName];
        RaiseDirectoryChange([profileName], [], deletedProfileNames);
    }

    private void RaisePendingDirectoryChange()
    {
        string[] profileNames;
        WatchAreaProfileRename[] renames;
        lock (_directoryWatchGate)
        {
            if (_disposed || _pendingChangedProfileNames.Count == 0)
            {
                return;
            }

            profileNames = [.. _pendingChangedProfileNames];
            renames = [.. _pendingProfileRenames];
            _pendingChangedProfileNames.Clear();
            _pendingProfileRenames.Clear();
        }

        profileNames = profileNames
            .Where(profileName => !ShouldSuppressOwnProfileWrite(profileName))
            .ToArray();
        RaiseDirectoryChange(profileNames, renames, []);
    }

    private void RaiseDirectoryChange(
        IReadOnlyList<string> profileNames,
        IReadOnlyList<WatchAreaProfileRename> renames,
        IReadOnlyList<string> deletedProfileNames,
        bool requiresFullRescan = false)
    {
        if (profileNames.Count == 0 && renames.Count == 0 && !requiresFullRescan)
        {
            return;
        }

        DirectoryChanged?.Invoke(
            this,
            new WatchAreaProfileDirectoryChange(
                profileNames,
                renames,
                deletedProfileNames,
                requiresFullRescan));
    }

    private IWatchAreaProfileDirectoryEventSource GetOrCreateDirectoryEventSourceNoLock()
    {
        if (_directoryEventSource is not null)
        {
            return _directoryEventSource;
        }

        _directoryEventSource = _configuredDirectoryEventSource
            ?? new WatchAreaProfileDirectoryWatcher();
        _directoryEventSource.Raised += OnDirectoryEventRaised;
        _directoryEventSource.Failed += OnDirectoryEventSourceFailed;
        return _directoryEventSource;
    }

    private void EnsureDirectoryWatchHealthTimerNoLock()
    {
        _directoryWatchHealthTimer ??= _timeProvider.CreateTimer(
            _ => CheckDirectoryWatchHealth(),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _directoryWatchHealthTimer.Change(
            DirectoryWatchHealthCheckInterval,
            DirectoryWatchHealthCheckInterval);
    }

    private void TryStartDirectoryEventSource(
        IWatchAreaProfileDirectoryEventSource source,
        bool failureRescanAlreadyEmitted)
    {
        try
        {
            source.Start(DirectoryPath);
            bool requiresFullRescan;
            lock (_directoryWatchGate)
            {
                if (_disposed
                    || _directoryWatchLifecycle is not DirectoryWatchLifecycle.Starting)
                {
                    source.Stop();
                    return;
                }

                _directoryWatchLifecycle = DirectoryWatchLifecycle.Watching;
                requiresFullRescan = _directoryWatchNeedsFullRescan;
                _directoryWatchNeedsFullRescan = false;
            }

            RaiseDirectoryWatchStateChanged(
                WatchAreaProfileDirectoryWatchStatus.Watching,
                "AREA 配置目录监视正常。");
            if (requiresFullRescan)
            {
                RaiseDirectoryChange([], [], [], requiresFullRescan: true);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            EnterDirectoryWatchDegraded(
                $"AREA 配置目录暂时无法监视：{exception.Message}",
                requiresFullRescan: !failureRescanAlreadyEmitted);
            lock (_directoryWatchGate)
            {
                if (!_disposed
                    && _directoryWatchLifecycle is DirectoryWatchLifecycle.Degraded)
                {
                    _directoryWatchNeedsFullRescan = true;
                }
            }
        }
    }

    private void OnDirectoryEventSourceFailed(
        object? sender,
        WatchAreaProfileDirectoryFailure failure)
    {
        EnterDirectoryWatchDegraded(
            failure.Exception is InternalBufferOverflowException
                ? "AREA 配置目录事件过多，正在全量重新扫描并重建监视。"
                : $"AREA 配置目录监视已中断：{failure.Exception.Message}");
        if (Directory.Exists(DirectoryPath))
        {
            TryRecoverDirectoryWatch(failureRescanAlreadyEmitted: true);
        }
    }

    private void CheckDirectoryWatchHealth()
    {
        DirectoryWatchLifecycle lifecycle;
        lock (_directoryWatchGate)
        {
            if (_disposed)
            {
                return;
            }

            lifecycle = _directoryWatchLifecycle;
        }

        if (lifecycle is DirectoryWatchLifecycle.Watching
            && Directory.Exists(DirectoryPath))
        {
            return;
        }

        if (lifecycle is DirectoryWatchLifecycle.Watching)
        {
            EnterDirectoryWatchDegraded(
                "AREA 配置目录不存在；目录恢复后将自动重新监视。");
            return;
        }

        if (lifecycle is DirectoryWatchLifecycle.Degraded)
        {
            TryRecoverDirectoryWatch(failureRescanAlreadyEmitted: false);
        }
    }

    private void TryRecoverDirectoryWatch(bool failureRescanAlreadyEmitted)
    {
        IWatchAreaProfileDirectoryEventSource? source;
        lock (_directoryWatchGate)
        {
            if (_disposed
                || _directoryWatchLifecycle is not DirectoryWatchLifecycle.Degraded
                || !Directory.Exists(DirectoryPath))
            {
                return;
            }

            _directoryWatchLifecycle = DirectoryWatchLifecycle.Starting;
            source = _directoryEventSource;
        }

        if (source is not null)
        {
            TryStartDirectoryEventSource(source, failureRescanAlreadyEmitted);
        }
    }

    private void EnterDirectoryWatchDegraded(
        string message,
        bool requiresFullRescan = true)
    {
        IWatchAreaProfileDirectoryEventSource? source;
        bool shouldStopSource;
        lock (_directoryWatchGate)
        {
            if (_disposed
                || _directoryWatchLifecycle is DirectoryWatchLifecycle.Stopped)
            {
                return;
            }

            shouldStopSource = _directoryWatchLifecycle is
                DirectoryWatchLifecycle.Watching or DirectoryWatchLifecycle.Starting;
            _directoryWatchLifecycle = DirectoryWatchLifecycle.Degraded;
            if (requiresFullRescan)
            {
                _directoryWatchNeedsFullRescan = !Directory.Exists(DirectoryPath);
            }
            source = _directoryEventSource;
        }

        if (shouldStopSource)
        {
            source?.Stop();
        }

        RaiseDirectoryWatchStateChanged(
            WatchAreaProfileDirectoryWatchStatus.Degraded,
            message);
        if (requiresFullRescan)
        {
            RaiseDirectoryChange([], [], [], requiresFullRescan: true);
        }
    }

    private void RaiseDirectoryWatchStateChanged(
        WatchAreaProfileDirectoryWatchStatus status,
        string message) => DirectoryWatchStateChanged?.Invoke(
            this,
            new WatchAreaProfileDirectoryWatchStateChange(status, message));

    private enum DirectoryWatchLifecycle
    {
        Stopped,
        Starting,
        Watching,
        Degraded,
    }

    private void RememberOwnProfileWrite(WatchAreaFilterProfile profile)
    {
        if (profile.FileFingerprint is not { } fingerprint)
        {
            return;
        }

        lock (_directoryWatchGate)
        {
            if (_disposed || _directoryEventSource is null)
            {
                return;
            }

            _ownProfileWrites[profile.ProfileName] = new OwnProfileWrite(
                fingerprint,
                _timeProvider.GetUtcNow() + OwnWriteSuppressionWindow);
        }
    }

    private bool ShouldSuppressOwnProfileWrite(string profileName)
    {
        OwnProfileWrite ownWrite;
        lock (_directoryWatchGate)
        {
            if (!_ownProfileWrites.TryGetValue(profileName, out ownWrite!))
            {
                return false;
            }

            if (ownWrite.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _ownProfileWrites.Remove(profileName);
                return false;
            }
        }

        string currentFingerprint;
        try
        {
            currentFingerprint = ReadProfileFileSnapshot(GetProfilePath(profileName)).Fingerprint;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }

        if (string.Equals(
                currentFingerprint,
                ownWrite.Fingerprint,
                StringComparison.Ordinal))
        {
            return true;
        }

        lock (_directoryWatchGate)
        {
            if (_ownProfileWrites.TryGetValue(profileName, out var current)
                && current == ownWrite)
            {
                _ownProfileWrites.Remove(profileName);
            }
        }

        return false;
    }

    /// <summary>
    /// Revalidates the extension in managed code: the watcher extension filter
    /// also matches 8.3 short names, so <c>plan.txtbackup</c> can arrive as a
    /// <c>*.txt</c> event.
    /// </summary>
    private static bool IsProfileFileName(string fileName) =>
        fileName.Length != 0
        && !fileName.StartsWith('.')
        && Path.GetExtension(fileName).Equals(ProfileExtension, StringComparison.OrdinalIgnoreCase)
        && WatchAreaFilterProfileParser.IsSafeProfileName(
            Path.GetFileNameWithoutExtension(fileName));

    private static bool HasCompanionFileAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Hidden)
                || attributes.HasFlag(FileAttributes.System);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record ProfileFileSnapshot(byte[] Bytes, string Fingerprint);

    private sealed record OwnProfileWrite(string Fingerprint, DateTimeOffset ExpiresAt);

    private sealed class ExternalProfileLoadRetry(
        WatchAreaFilterProfileStore store,
        string profileName,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<WatchAreaFilterProfile> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer? _timer;
        private CancellationTokenRegistration _cancellationRegistration;
        private int _nextDelayIndex;
        private int _completed;

        public Task<WatchAreaFilterProfile> Start()
        {
            var registration = cancellationToken.Register(
                () => CompleteCanceled(cancellationToken));
            _cancellationRegistration = registration;
            if (Volatile.Read(ref _completed) != 0)
            {
                registration.Dispose();
            }
            else
            {
                TryLoad();
            }

            return _completion.Task;
        }

        private void TryLoad()
        {
            if (Volatile.Read(ref _completed) != 0)
            {
                return;
            }

            try
            {
                var loaded = store.Load(profileName);
                var transientDiagnostic = loaded.Diagnostics.FirstOrDefault(diagnostic =>
                    diagnostic.Code is WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound
                        or WatchAreaFilterProfileDiagnosticCodes.InvalidUtf8);
                if (transientDiagnostic is not null)
                {
                    throw new IOException(transientDiagnostic.Message);
                }

                Complete(loaded);
            }
            catch (IOException exception)
            {
                if (_nextDelayIndex >= ExternalReadRetryDelays.Count)
                {
                    CompleteException(exception);
                    return;
                }

                _timer ??= store._timeProvider.CreateTimer(
                    static state => ((ExternalProfileLoadRetry)state!).TryLoad(),
                    this,
                    Timeout.InfiniteTimeSpan,
                    Timeout.InfiniteTimeSpan);
                _timer.Change(
                    ExternalReadRetryDelays[_nextDelayIndex++],
                    Timeout.InfiniteTimeSpan);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or ArgumentException)
            {
                CompleteException(exception);
            }
        }

        private void Complete(WatchAreaFilterProfile profile)
        {
            if (!TryFinish())
            {
                return;
            }

            _completion.TrySetResult(profile);
        }

        private void CompleteException(Exception exception)
        {
            if (!TryFinish())
            {
                return;
            }

            _completion.TrySetException(exception);
        }

        private void CompleteCanceled(CancellationToken token)
        {
            if (!TryFinish())
            {
                return;
            }

            _completion.TrySetCanceled(token);
        }

        private bool TryFinish()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return false;
            }

            _timer?.Dispose();
            _cancellationRegistration.Dispose();
            return true;
        }
    }

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
