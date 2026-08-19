using System.Globalization;
using System.Text.Json;

namespace MesIngest.Core;

/// <summary>
/// Serves recorded MES_TASK_UNION result sets so a release smoke can drive
/// repeatable rounds through the production entry when no factory Oracle is
/// reachable. Everything after the statement — canonical artifact, round source,
/// projection commit — stays production code.
///
/// It attests nothing about a database: <see cref="RuntimeState"/> stays
/// <see cref="OracleExecutorRuntimeState.Unverified"/> and the driver reports
/// FILE_REPLAY, so probe and factory evidence still classify these rounds as
/// NOT_EXECUTED rather than a live plant read.
/// </summary>
public sealed class ReplayedMesTaskUnionStatementExecutor : IOracleStatementExecutor
{
    public const string DriverName = "FILE_REPLAY";
    private const string InvalidRecordingCode = "REPLAY_RECORDING_INVALID";

    private readonly string _queryVersion;
    private readonly IReadOnlyList<OracleResultColumn> _columns;
    private readonly IReadOnlyList<IReadOnlyList<IReadOnlyList<object?>>> _rounds;
    private int _served;

    private ReplayedMesTaskUnionStatementExecutor(
        string queryVersion,
        IReadOnlyList<OracleResultColumn> columns,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<object?>>> rounds,
        OracleClientMode requestedMode)
    {
        _queryVersion = queryVersion;
        _columns = columns;
        _rounds = rounds;
        Identity = new OracleExecutorIdentity(requestedMode, requestedMode, DriverName);
    }

    public OracleExecutorIdentity Identity { get; }

    public OracleExecutorRuntimeState RuntimeState => OracleExecutorRuntimeState.Unverified;

    /// <summary>
    /// Reads a recording file. Failures are configuration errors with a stable code
    /// and a message that names no path, so a smoke log cannot leak deployment layout.
    /// </summary>
    public static ReplayedMesTaskUnionStatementExecutor Load(
        string recordingPath,
        OracleClientMode requestedMode)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(recordingPath));
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or ArgumentException
                                              or JsonException)
        {
            throw Invalid();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.Number
                || schemaVersion.GetInt32() != 1
                || !root.TryGetProperty("queryVersion", out var queryVersion)
                || queryVersion.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("columns", out var columns)
                || columns.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("rounds", out var rounds)
                || rounds.ValueKind != JsonValueKind.Array
                || rounds.GetArrayLength() == 0)
            {
                throw Invalid();
            }

            var parsedColumns = ParseColumns(columns);
            var parsedRounds = new List<IReadOnlyList<IReadOnlyList<object?>>>(rounds.GetArrayLength());
            foreach (var round in rounds.EnumerateArray())
            {
                parsedRounds.Add(ParseRows(round, parsedColumns));
            }

            return new ReplayedMesTaskUnionStatementExecutor(
                queryVersion.GetString()!,
                parsedColumns,
                parsedRounds,
                requestedMode);
        }
    }

    public Task<OracleStatementResult> ExecuteAsync(
        OracleStatementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.QueryVersion, _queryVersion, StringComparison.Ordinal))
        {
            throw new OracleProviderConfigurationException(
                "REPLAY_QUERY_VERSION_MISMATCH",
                "The recorded rounds were captured for a different approved query version.");
        }

        // A continuous poll loop outlives the recording. Holding the final round keeps
        // the projection stable while each poll still produces a distinct PollTrace.
        var index = Math.Min(Interlocked.Increment(ref _served) - 1, _rounds.Count - 1);
        return Task.FromResult(new OracleStatementResult(_columns, _rounds[index]));
    }

    private static IReadOnlyList<OracleResultColumn> ParseColumns(JsonElement columns)
    {
        if (columns.GetArrayLength() == 0)
        {
            throw Invalid();
        }

        var parsed = new List<OracleResultColumn>(columns.GetArrayLength());
        foreach (var column in columns.EnumerateArray())
        {
            if (column.ValueKind != JsonValueKind.Object
                || !column.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString())
                || !column.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<OracleColumnKind>(kind.GetString(), ignoreCase: false, out var parsedKind))
            {
                throw Invalid();
            }

            parsed.Add(new OracleResultColumn(name.GetString()!, DriverName, parsedKind));
        }

        return parsed;
    }

    private static IReadOnlyList<IReadOnlyList<object?>> ParseRows(
        JsonElement round,
        IReadOnlyList<OracleResultColumn> columns)
    {
        if (round.ValueKind != JsonValueKind.Object
            || !round.TryGetProperty("rows", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        var parsed = new List<IReadOnlyList<object?>>(rows.GetArrayLength());
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array
                || row.GetArrayLength() != columns.Count)
            {
                throw Invalid();
            }

            var values = new List<object?>(columns.Count);
            var ordinal = 0;
            foreach (var value in row.EnumerateArray())
            {
                values.Add(ParseValue(value, columns[ordinal].Kind));
                ordinal++;
            }

            parsed.Add(values);
        }

        return parsed;
    }

    private static object? ParseValue(JsonElement value, OracleColumnKind kind)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }

        var text = value.GetString()!;
        return kind switch
        {
            OracleColumnKind.Text => text,
            // Unparsable date text stays raw evidence, exactly as a live provider
            // would hand a string column through to the round source.
            OracleColumnKind.DateTimeOffset or OracleColumnKind.DateTime =>
                DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed)
                    ? parsed
                    : text,
            _ => throw Invalid(),
        };
    }

    private static OracleProviderConfigurationException Invalid() => new(
        InvalidRecordingCode,
        "The recorded MES_TASK_UNION rounds file is missing or does not match the recording contract.");
}
