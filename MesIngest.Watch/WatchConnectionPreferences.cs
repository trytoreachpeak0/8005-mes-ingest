using System.IO;
using System.Text.Json;

namespace MesIngest.Watch;

internal static class WatchCredentialReference
{
    /// <summary>
    /// The credential remains in the existing external configuration boundary
    /// (MesIngestWatch__SharedSecret or appsettings.Local.json); no secret is persisted here.
    /// </summary>
    public const string ExternalConfiguration = "external-configuration";
}

internal sealed record WatchConnectionPreferences(
    string BaseUrl,
    int RequestTimeoutSeconds,
    string CredentialReference)
{
    public static WatchConnectionPreferences Default { get; } = new(
        "http://127.0.0.1:5088",
        30,
        WatchCredentialReference.ExternalConfiguration);

    public static WatchConnectionPreferences FromOptions(WatchOptions options) => new(
        options.BaseUrl,
        options.RequestTimeoutSeconds,
        WatchCredentialReference.ExternalConfiguration);
}

internal static class WatchConnectionPreferencesStore
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MesIngest.Watch",
        "connection-preferences.json");

    public static WatchConnectionPreferences Load(
        string path,
        WatchConnectionPreferences fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            var document = JsonSerializer.Deserialize<PreferenceDocument>(
                File.ReadAllText(path),
                JsonOptions);
            return document is not null
                && document.Version == CurrentVersion
                && IsSafe(document.BaseUrl, document.RequestTimeoutSeconds, document.CredentialReference)
                ? new WatchConnectionPreferences(
                    document.BaseUrl!.TrimEnd('/'),
                    document.RequestTimeoutSeconds,
                    document.CredentialReference!)
                : fallback;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException)
        {
            return fallback;
        }
    }

    public static void Save(string path, WatchConnectionPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (!IsSafe(
                preferences.BaseUrl,
                preferences.RequestTimeoutSeconds,
                preferences.CredentialReference))
        {
            throw new ArgumentException("Connection preferences contain an unsafe or invalid value.", nameof(preferences));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new PreferenceDocument(
            CurrentVersion,
            preferences.BaseUrl.TrimEnd('/'),
            preferences.RequestTimeoutSeconds,
            WatchCredentialReference.ExternalConfiguration);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    private static bool IsSafe(string? baseUrl, int timeoutSeconds, string? credentialReference) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && timeoutSeconds is >= 1 and <= 300
        && string.Equals(
            credentialReference,
            WatchCredentialReference.ExternalConfiguration,
            StringComparison.Ordinal);

    private sealed record PreferenceDocument(
        int Version,
        string? BaseUrl,
        int RequestTimeoutSeconds,
        string? CredentialReference);
}
