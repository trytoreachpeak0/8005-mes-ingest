namespace MesIngest.Host;

/// <summary>
/// Decides whether configured Kestrel URLs are localhost-only.
/// Non-localhost bindings require a shared secret (ticket 10 / phase-1 auth).
/// </summary>
public static class ListenBindingPolicy
{
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