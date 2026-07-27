using System.Diagnostics;

namespace MesIngest.Core;

/// <summary>
/// One-shot factory connectivity probe for Oracle MES_TASK_UNION.
/// Does not claim plant success — only exercises the configured source once.
/// </summary>
public static class OracleProbe
{
    public static async Task<int> RunAsync(
        IMesSnapshotSource source,
        TextWriter output,
        OracleClientMode requestedMode,
        bool? instantClientOnPath = null,
        CancellationToken cancellationToken = default)
    {
        output.WriteLine("MesIngest Oracle probe");
        output.WriteLine("driver=Oracle.ManagedDataAccess.Core");
        output.WriteLine("driver_kind=managed");
        output.WriteLine($"requested_mode={requestedMode}");
        if (instantClientOnPath is not null)
        {
            output.WriteLine($"instant_client_on_path={instantClientOnPath.Value}");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await source.ReadAsync(cancellationToken);
            sw.Stop();
            output.WriteLine($"duration_ms={sw.Elapsed.TotalMilliseconds:F0}");
            output.WriteLine($"outcome={outcome.Kind}");
            output.WriteLine($"row_count={outcome.Rows.Count}");

            if (outcome.Kind == SnapshotOutcomeKind.Success)
            {
                output.WriteLine("probe_result=ok");
                return 0;
            }

            output.WriteLine("probe_result=failed");
            return 2;
        }
        catch (Exception ex)
        {
            sw.Stop();
            output.WriteLine($"duration_ms={sw.Elapsed.TotalMilliseconds:F0}");
            output.WriteLine($"outcome=Exception");
            output.WriteLine($"error={Sanitize(ex.Message)}");
            output.WriteLine("probe_result=failed");
            return 1;
        }
    }

    /// <summary>
    /// Strip common secret-bearing fragments from probe error text.
    /// Never print passwords or full connection strings.
    /// </summary>
    internal static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var sanitized = message;
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(Password\s*=\s*)([^;]+)",
            "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        sanitized = System.Text.RegularExpressions.Regex.Replace(
            sanitized,
            @"(Pwd\s*=\s*)([^;]+)",
            "$1***",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return sanitized;
    }
}
