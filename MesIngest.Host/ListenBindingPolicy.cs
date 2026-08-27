namespace MesIngest.Host;

/// <summary>
/// Immutable startup decision used by request admission. The URL authority is
/// validated once, before Kestrel is built, and is never inferred again from
/// mutable configuration while requests are in flight.
/// </summary>
public sealed class BindingSecurityPolicy
{
    internal BindingSecurityPolicy(
        string urls,
        bool requiresSharedSecret,
        string sharedSecret)
    {
        Urls = urls;
        RequiresSharedSecret = requiresSharedSecret;
        SharedSecret = sharedSecret;
    }

    public string Urls { get; }

    public bool RequiresSharedSecret { get; }

    internal string SharedSecret { get; }
}

/// <summary>
/// Builds the only supported Kestrel listen policy from <c>MesIngest:Urls</c>.
/// Alternate ASP.NET/Kestrel endpoint authorities are rejected rather than
/// allowed to override the URL on which the security decision was based.
/// </summary>
public static class ListenBindingPolicy
{
    private static readonly string[] AlternateUrlKeys =
    [
        "urls",
        "http_ports",
        "https_ports",
        "ASPNETCORE_URLS",
        "ASPNETCORE_HTTP_PORTS",
        "ASPNETCORE_HTTPS_PORTS",
        "DOTNET_URLS",
        "DOTNET_HTTP_PORTS",
        "DOTNET_HTTPS_PORTS",
    ];

    public static BindingSecurityPolicy Create(
        MesIngestHostOptions options,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            throw new InvalidOperationException(
                "Kestrel:Endpoints is not supported. MesIngest:Urls is the sole listen authority.");
        }

        foreach (var key in AlternateUrlKeys)
        {
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                throw new InvalidOperationException(
                    $"Alternate endpoint authority '{key}' is not supported. "
                    + "MesIngest:Urls is the sole listen authority.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Urls))
        {
            throw new InvalidOperationException(
                "MesIngest:Urls is required and is the sole listen authority.");
        }

        var urls = options.Urls.Trim();
        return new BindingSecurityPolicy(
            urls,
            RequiresSharedSecret(urls),
            options.SharedSecret);
    }

    public static bool RequiresSharedSecret(string? urls) => !IsLocalhostOnly(urls);

    public static bool IsLocalhostOnly(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls))
        {
            return true;
        }

        foreach (var part in urls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsLocalhostUrl(part))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLocalhostUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            var host = string.IsNullOrEmpty(uri.IdnHost) ? uri.Host : uri.IdnHost;
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                   || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                   || host.Equals("::1", StringComparison.OrdinalIgnoreCase);
        }

        // ASP.NET shorthand binds (http://*:5088, http://+:5088, http://0.0.0.0:5088)
        // are not absolute URIs and are never localhost-only.
        return false;
    }
}
