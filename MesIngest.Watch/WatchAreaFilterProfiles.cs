using System.IO;
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
    public const string ProfileNameRequired = "PROFILE_NAME_REQUIRED";
    public const string TooManyMesAreas = "TOO_MANY_MES_AREAS";
    public const string UnsafeProfileName = "UNSAFE_PROFILE_NAME";
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

    public WatchAreaFilterProfileStore(
        string? directoryPath = null,
        TimeProvider? timeProvider = null)
    {
        var selectedPath = directoryPath ?? DefaultDirectoryPath;
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        DirectoryPath = Path.GetFullPath(selectedPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string DefaultDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesIngest.Watch",
        "area-filters");

    public string DirectoryPath { get; }

    public string ActiveMarkerPath => Path.Combine(DirectoryPath, ActiveMarkerFileName);

    public IReadOnlyList<WatchAreaFilterProfileSummary> EnumerateProfiles()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var appliedProfileName = LoadApplied().ProfileName;
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
                    StringComparison.Ordinal)))
            .ToArray();
    }

    public WatchAreaFilterProfile Load(string? profileName)
    {
        var nameCheck = WatchAreaFilterProfileParser.Parse(profileName, "A1-1");
        if (!nameCheck.IsValid)
        {
            return nameCheck with { Content = string.Empty, MesAreas = [] };
        }

        var path = GetProfilePath(nameCheck.ProfileName);
        if (!File.Exists(path))
        {
            return UnavailableProfile(
                nameCheck.ProfileName,
                WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound,
                "AREA 配置 TXT 文件不存在。");
        }

        try
        {
            var content = File.ReadAllText(path, StrictUtf8);
            return WatchAreaFilterProfileParser.Parse(nameCheck.ProfileName, content);
        }
        catch (DecoderFallbackException)
        {
            return UnavailableProfile(
                nameCheck.ProfileName,
                WatchAreaFilterProfileDiagnosticCodes.InvalidUtf8,
                "AREA 配置 TXT 文件不是有效的 UTF-8。");
        }
        catch (FileNotFoundException)
        {
            return UnavailableProfile(
                nameCheck.ProfileName,
                WatchAreaFilterProfileDiagnosticCodes.ProfileNotFound,
                "AREA 配置 TXT 文件不存在。");
        }
    }

    public WatchAreaFilterProfileSaveResult Save(string? profileName, string content)
    {
        var draft = WatchAreaFilterProfileParser.Parse(profileName, content);
        if (!draft.IsValid)
        {
            return new WatchAreaFilterProfileSaveResult(Saved: false, draft);
        }

        AtomicWriteText(GetProfilePath(draft.ProfileName), draft.Content);
        return new WatchAreaFilterProfileSaveResult(Saved: true, draft);
    }

    public WatchAreaFilterProfileApplyResult Apply(string? profileName)
    {
        var draft = Load(profileName);
        return ApplyDraft(draft);
    }

    public WatchAreaFilterProfileApplyResult Apply(string? profileName, string content)
    {
        var saved = Save(profileName, content);
        return saved.Saved
            ? ApplyDraft(saved.Draft)
            : new WatchAreaFilterProfileApplyResult(
                Applied: false,
                saved.Draft,
                LoadApplied());
    }

    public WatchAreaFilterAllAreasApplyResult ApplyAllAreas()
    {
        var appliedAt = _timeProvider.GetUtcNow();
        WriteActiveMarker(new ActiveMarkerDocument(
            ActiveMarkerVersion,
            ProfileName: null,
            MesAreas: [],
            AppliedAt: appliedAt));
        return new WatchAreaFilterAllAreasApplyResult(
            new WatchAppliedAreaFilterProfile(null, [], appliedAt));
    }

    public WatchAppliedAreaFilterProfile LoadApplied() =>
        LoadAppliedState().CurrentApplied;

    public WatchAreaFilterAppliedLoadResult LoadAppliedState()
    {
        if (!File.Exists(ActiveMarkerPath))
        {
            return new WatchAreaFilterAppliedLoadResult(
                WatchAppliedAreaFilterProfile.AllAreas,
                Diagnostic: null);
        }

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

    private WatchAreaFilterProfileApplyResult ApplyDraft(WatchAreaFilterProfile draft)
    {
        if (!draft.IsValid)
        {
            return new WatchAreaFilterProfileApplyResult(
                Applied: false,
                draft,
                LoadApplied());
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

    private void WriteActiveMarker(ActiveMarkerDocument marker)
    {
        var content = JsonSerializer.Serialize(marker, MarkerJsonOptions);
        AtomicWriteText(ActiveMarkerPath, content);
    }

    private static WatchAreaFilterProfile UnavailableProfile(
        string profileName,
        string code,
        string message) => new(
        profileName,
        string.Empty,
        [],
        [new WatchAreaFilterProfileDiagnostic(code, message)]);

    private string GetProfilePath(string profileName) =>
        Path.Combine(DirectoryPath, $"{profileName}{ProfileExtension}");

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
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
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
