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

    public static BindingSecurityPolicy ValidateStartup(
        MesIngestHostOptions options,
        IConfiguration configuration)
    {
        var policy = ListenBindingPolicy.Create(options, configuration);
        if (!policy.RequiresSharedSecret)
        {
            return policy;
        }

        if (string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            throw new InvalidOperationException(
                "Listen URLs bind beyond localhost; set MesIngest:SharedSecret "
                + "(Authorization: Bearer <secret>) or bind only 127.0.0.1/localhost.");
        }

        return policy;
    }

    public static bool IsAuthorized(
        HttpRequest request,
        BindingSecurityPolicy policy)
    {
        if (!policy.RequiresSharedSecret)
        {
            return true;
        }

        return IsExplicitlyAuthorized(request, policy.SharedSecret);
    }

    /// <summary>
    /// Requires the configured shared secret even when the host is bound only to
    /// localhost. Restricted raw evidence must never inherit the normal local-read
    /// bypass from <see cref="IsAuthorized"/>.
    /// </summary>
    public static bool IsExplicitlyAuthorized(
        HttpRequest request,
        MesIngestHostOptions options) =>
        IsExplicitlyAuthorized(request, options.SharedSecret);

    private static bool IsExplicitlyAuthorized(
        HttpRequest request,
        string sharedSecret)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret))
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
        return FixedTimeEquals(presented, sharedSecret);
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
