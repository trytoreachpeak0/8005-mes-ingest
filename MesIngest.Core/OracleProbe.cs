using System.Diagnostics;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Core;

public enum OracleProbeExecutionScope
{
    LiveOracle,
    OfflineArtifactOnly,
    Simulated,
    NotExecuted,
}

/// <summary>
/// Declares what a probe actually exercised. A non-live scope is deliberately
/// unable to produce a factory pass.
/// </summary>
public sealed record OracleRoundProbeIdentity(
    OracleProbeExecutionScope ExecutionScope,
    bool ConnectionAttempted,
    OracleClientMode RequestedMode,
    OracleClientMode? ActualMode,
    string Driver)
{
    public static OracleRoundProbeIdentity Live(
        OracleClientMode requestedMode,
        OracleClientMode actualMode,
        string driver)
    {
        if (requestedMode != actualMode)
        {
            throw new ArgumentException(
                "The live Oracle probe cannot silently substitute another provider mode.",
                nameof(actualMode));
        }

        return new(OracleProbeExecutionScope.LiveOracle, true, requestedMode, actualMode, driver);
    }

    public static OracleRoundProbeIdentity NotExecuted(
        OracleProbeExecutionScope executionScope,
        OracleClientMode requestedMode,
        string driver)
    {
        if (executionScope == OracleProbeExecutionScope.LiveOracle)
        {
            throw new ArgumentException(
                "A not-executed probe cannot declare live Oracle scope.",
                nameof(executionScope));
        }

        return new(executionScope, false, requestedMode, null, driver);
    }
}

/// <summary>Safe, independently importable state for one Thin or Thick probe log.</summary>
public sealed record OracleRoundProbeManifestState(
    bool Attempted,
    bool ConnectionAttempted,
    string ExecutionScope,
    string RequestedMode,
    string ActualMode,
    string Result,
    string Log)
{
    public static OracleRoundProbeManifestState FromOutput(string log, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(log);
        ArgumentNullException.ThrowIfNull(output);

        var fields = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > 0)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Last()[1],
                StringComparer.OrdinalIgnoreCase);

        static string Read(
            IReadOnlyDictionary<string, string> values,
            string key,
            string fallback = "UNKNOWN") =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;

        var scope = Read(fields, "execution_scope", "NOT_EXECUTED").ToUpperInvariant();
        var connectionAttempted = bool.TryParse(
            Read(fields, "connection_attempted", "false"),
            out var attempted) && attempted;
        var requestedMode = Read(fields, "requested_mode");
        var actualMode = Read(fields, "actual_mode", "NOT_AVAILABLE");
        var queryId = Read(fields, "query_id", "NOT_AVAILABLE");
        var queryVersion = Read(fields, "query_version", "NOT_AVAILABLE");
        var querySha256 = Read(fields, "query_sha256", "NOT_AVAILABLE");
        var outcome = Read(fields, "outcome", "NOT_EXECUTED");
        var result = Read(fields, "result", "NOT_EXECUTED").ToUpperInvariant();
        if (result is not ("PASSED" or "FAILED" or "NOT_EXECUTED"))
        {
            result = "FAILED";
        }
        else if (result == "PASSED"
            && (scope != "LIVE_ORACLE" || !connectionAttempted))
        {
            result = "NOT_EXECUTED";
        }
        else if (result == "PASSED"
            && !string.Equals(requestedMode, actualMode, StringComparison.OrdinalIgnoreCase))
        {
            result = "FAILED";
        }
        else if (result == "PASSED"
            && (!string.Equals(outcome, "Success", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(queryId, CanonicalMesTaskUnionQuery.Id, StringComparison.Ordinal)
                || !string.Equals(queryVersion, CanonicalMesTaskUnionQuery.QueryVersion, StringComparison.Ordinal)
                || !string.Equals(querySha256, CanonicalMesTaskUnionQuery.ExpectedSha256, StringComparison.Ordinal)))
        {
            result = "FAILED";
        }

        return new OracleRoundProbeManifestState(
            Attempted: true,
            ConnectionAttempted: connectionAttempted,
            ExecutionScope: scope,
            RequestedMode: requestedMode,
            ActualMode: actualMode,
            Result: result,
            Log: log);
    }
}

/// <summary>
/// One-shot factory connectivity probe for Oracle MES_TASK_UNION.
/// Does not claim plant success — only exercises the configured source once.
/// </summary>
public static class OracleProbe
{
    public static async Task<int> RunAsync(
        OracleMesTaskUnionRoundSource source,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        var requestedMode = source.RequestedMode;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var round = await source.ReadRoundAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var selected = source.ExecutorIdentity;
            var runtimeState = source.ExecutorRuntimeState;
            var identity = CreateRuntimeIdentity(requestedMode, selected, runtimeState);
            if (selected is null
                || !runtimeState.ConnectionAttempted
                || !runtimeState.CanAttestLiveOracle)
            {
                WriteRoundHeader(output, identity);
                output.WriteLine($"duration_ms={stopwatch.Elapsed.TotalMilliseconds:F0}");
                output.WriteLine("outcome=NOT_EXECUTED");
                output.WriteLine("row_count=0");
                if (round.Diagnostic is not null)
                {
                    output.WriteLine($"diagnostic_stage={SafeToken(round.Diagnostic.Stage)}");
                    output.WriteLine($"diagnostic_code={SafeToken(round.Diagnostic.Code)}");
                }
                output.WriteLine("result=NOT_EXECUTED");
                return 3;
            }
            WriteRoundHeader(output, identity);
            output.WriteLine($"duration_ms={stopwatch.Elapsed.TotalMilliseconds:F0}");
            output.WriteLine($"outcome={round.Outcome}");
            output.WriteLine($"row_count={round.Observations.Count}");
            if (round.Diagnostic is not null)
            {
                output.WriteLine($"diagnostic_stage={SafeToken(round.Diagnostic.Stage)}");
                output.WriteLine($"diagnostic_code={SafeToken(round.Diagnostic.Code)}");
            }

            var providerMatches = selected is not null
                && selected.RequestedMode == requestedMode
                && selected.ActualMode == requestedMode;
            if (round.Outcome == MesTaskUnionRoundOutcome.Success && providerMatches)
            {
                output.WriteLine("result=PASSED");
                return 0;
            }

            output.WriteLine("result=FAILED");
            return 2;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var selected = source.ExecutorIdentity;
            var runtimeState = source.ExecutorRuntimeState;
            WriteRoundHeader(
                output,
                CreateRuntimeIdentity(requestedMode, selected, runtimeState));
            output.WriteLine($"duration_ms={stopwatch.Elapsed.TotalMilliseconds:F0}");
            output.WriteLine("outcome=Exception");
            output.WriteLine($"error_type={exception.GetType().Name}");
            output.WriteLine("row_count=0");
            if (!runtimeState.ConnectionAttempted || !runtimeState.CanAttestLiveOracle)
            {
                output.WriteLine("result=NOT_EXECUTED");
                return 3;
            }

            output.WriteLine("result=FAILED");
            return 1;
        }
    }

    /// <summary>
    /// Probe the V2 causal-round source. Only a successful round reached through
    /// LIVE_ORACLE scope can return PASSED; artifact-only and simulated runs are
    /// explicit NOT_EXECUTED evidence.
    /// </summary>
    public static Task<int> RunRoundAsync(
        IMesTaskUnionRoundSource source,
        TextWriter output,
        OracleRoundProbeIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(identity);

        WriteRoundHeader(output, identity);

        // A generic source and caller-supplied identity can never attest a live
        // provider. Only the concrete Oracle source overload consumes executor
        // runtime evidence. Do not execute the supplied source or expose its data.
        cancellationToken.ThrowIfCancellationRequested();
        output.WriteLine("duration_ms=0");
        output.WriteLine(identity.ExecutionScope == OracleProbeExecutionScope.LiveOracle
            || identity.ConnectionAttempted
                ? "outcome=SIMULATED_IDENTITY_REJECTED"
                : "outcome=NOT_EXECUTED");
        output.WriteLine("row_count=0");
        output.WriteLine("result=NOT_EXECUTED");
        return Task.FromResult(3);
    }

    private static string FormatScope(OracleProbeExecutionScope scope) => scope switch
    {
        OracleProbeExecutionScope.LiveOracle => "LIVE_ORACLE",
        OracleProbeExecutionScope.OfflineArtifactOnly => "OFFLINE_ARTIFACT_ONLY",
        OracleProbeExecutionScope.Simulated => "SIMULATED",
        OracleProbeExecutionScope.NotExecuted => "NOT_EXECUTED",
        _ => "UNKNOWN",
    };

    private static OracleRoundProbeIdentity CreateRuntimeIdentity(
        OracleClientMode requestedMode,
        OracleExecutorIdentity? selected,
        OracleExecutorRuntimeState runtimeState)
    {
        var isAttestedLiveAttempt = selected is not null
            && runtimeState.ConnectionAttempted
            && runtimeState.CanAttestLiveOracle;
        return new OracleRoundProbeIdentity(
            isAttestedLiveAttempt
                ? OracleProbeExecutionScope.LiveOracle
                : OracleProbeExecutionScope.NotExecuted,
            runtimeState.ConnectionAttempted,
            requestedMode,
            selected?.ActualMode,
            selected?.Driver ?? "NOT_AVAILABLE");
    }

    private static void WriteRoundHeader(
        TextWriter output,
        OracleRoundProbeIdentity identity)
    {
        output.WriteLine("MesIngest Oracle MES_TASK_UNION round probe");
        output.WriteLine($"execution_scope={FormatScope(identity.ExecutionScope)}");
        output.WriteLine($"connection_attempted={identity.ConnectionAttempted.ToString().ToLowerInvariant()}");
        output.WriteLine($"requested_mode={identity.RequestedMode}");
        output.WriteLine($"actual_mode={identity.ActualMode?.ToString() ?? "NOT_AVAILABLE"}");
        output.WriteLine($"driver={SafeDriver(identity.Driver)}");
        output.WriteLine($"query_id={CanonicalMesTaskUnionQuery.Id}");
        output.WriteLine($"query_version={CanonicalMesTaskUnionQuery.QueryVersion}");
        output.WriteLine($"query_sha256={CanonicalMesTaskUnionQuery.ExpectedSha256}");
    }

    private static string SafeDriver(string driver)
    {
        if (string.IsNullOrWhiteSpace(driver))
        {
            return "NOT_AVAILABLE";
        }

        return SafeToken(driver);
    }

    private static string SafeToken(string value) =>
        new(value
            .Where(character => char.IsLetterOrDigit(character)
                || character is '.' or '-' or '_' or '(' or ')' or ' ')
            .Take(128)
            .ToArray());
}
