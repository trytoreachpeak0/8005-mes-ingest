using System.Security.Cryptography;
using System.Text;

namespace MesIngest.Host;

/// <summary>
/// Shared-secret gate for remote read-only HTTP and explicitly restricted raw
/// evidence reads, including when the host binds only to localhost.
/// Scheme: <c>Authorization: Bearer &lt;MesIngest:SharedSecret&gt;</c>.
/// </summary>
public static class SharedSecretAuth
{
    public const string BearerScheme = "Bearer";

    /// <summary>
    /// Prefer MesIngest:Urls; otherwise fall back to ASP.NET urls / ASPNETCORE_URLS
    /// so auth cannot be bypassed by clearing only the MesIngest section.
    /// </summary>
    public static string ResolveEffectiveUrls(MesIngestHostOptions options, IConfiguration? configuration = null)
    {
        if (!string.IsNullOrWhiteSpace(options.Urls))
        {
            return options.Urls;
        }

        var fromConfig = configuration?["urls"];
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }

        var fromEnv = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        return string.IsNullOrWhiteSpace(fromEnv) ? "" : fromEnv;
    }

    public static void ValidateStartup(MesIngestHostOptions options, IConfiguration? configuration = null)
    {
        var urls = ResolveEffectiveUrls(options, configuration);
        if (!ListenBindingPolicy.RequiresSharedSecret(urls))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            throw new InvalidOperationException(
                "Listen URLs bind beyond localhost; set MesIngest:SharedSecret "
                + "(Authorization: Bearer <secret>) or bind only 127.0.0.1/localhost.");
        }
    }

    public static bool IsAuthorized(
        HttpRequest request,
        MesIngestHostOptions options,
        IConfiguration? configuration = null)
    {
        var urls = ResolveEffectiveUrls(options, configuration);
        if (!ListenBindingPolicy.RequiresSharedSecret(urls))
        {
            return true;
        }

        return IsExplicitlyAuthorized(request, options);
    }

    /// <summary>
    /// Requires the configured shared secret even when the host is bound only to
    /// localhost. Restricted raw evidence must never inherit the normal local-read
    /// bypass from <see cref="IsAuthorized"/>.
    /// </summary>
    public static bool IsExplicitlyAuthorized(
        HttpRequest request,
        MesIngestHostOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            return false;
        }

        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        const string prefix = BearerScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var presented = header[prefix.Length..].Trim();
        return FixedTimeEquals(presented, options.SharedSecret);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
