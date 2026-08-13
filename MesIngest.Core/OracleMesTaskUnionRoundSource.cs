using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using MesIngest.Core.SeriesProjection;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace MesIngest.Core;

public interface IMesTaskUnionRoundSource
{
    Task<MesTaskUnionRound> ReadRoundAsync(CancellationToken cancellationToken = default);
}

public enum OracleColumnKind
{
    Text,
    DateTime,
    DateTimeOffset,
    Unsupported,
}

public sealed record OracleResultColumn(
    string Name,
    string ProviderTypeName,
    OracleColumnKind Kind);

public sealed record OracleStatementRequest(
    string Sql,
    string QueryVersion,
    string QuerySha256,
    int CommandTimeoutSeconds);

public sealed record OracleStatementResult(
    IReadOnlyList<OracleResultColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows);

public sealed record OracleExecutorIdentity(
    OracleClientMode RequestedMode,
    OracleClientMode ActualMode,
    string Driver);

/// <summary>
/// Runtime evidence produced by an Oracle executor for its most recent call.
/// Test doubles are deliberately unverified by default: provider selection is
/// not proof that a live Oracle connection was attempted.
/// </summary>
public sealed class OracleExecutorRuntimeState
{
    private OracleExecutorRuntimeState(bool connectionAttempted, bool canAttestLiveOracle)
    {
        ConnectionAttempted = connectionAttempted;
        CanAttestLiveOracle = canAttestLiveOracle;
    }

    public bool ConnectionAttempted { get; }

    public bool CanAttestLiveOracle { get; }

    public static OracleExecutorRuntimeState Unverified { get; } = new(false, false);

    internal static OracleExecutorRuntimeState Create(
        bool connectionAttempted,
        bool canAttestLiveOracle) => new(connectionAttempted, canAttestLiveOracle);
}

public interface IOracleStatementExecutor
{
    OracleExecutorIdentity Identity { get; }

    OracleExecutorRuntimeState RuntimeState => OracleExecutorRuntimeState.Unverified;

    Task<OracleStatementResult> ExecuteAsync(
        OracleStatementRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class OracleProviderConfigurationException : InvalidOperationException
{
    public OracleProviderConfigurationException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IOracleStatementExecutorFactory
{
    IOracleStatementExecutor Create(OracleSnapshotOptions options);
}

public sealed class OracleStatementExecutorFactory : IOracleStatementExecutorFactory
{
    public IOracleStatementExecutor Create(OracleSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode switch
        {
            OracleClientMode.Thin => new OdpNetOracleStatementExecutor(options),
            OracleClientMode.Thick => new OdbcOracleStatementExecutor(options),
            _ => throw new OracleProviderConfigurationException(
                "ORACLE_MODE_INVALID",
                "The configured Oracle provider mode is invalid."),
        };
    }
}

/// <summary>
/// Executes the one approved statement and turns its complete result into the
/// causal V2 round. Value anomalies stay SUCCESS evidence; only artifact,
/// execution, provider, or result-structure defects prevent projection.
/// </summary>
public sealed class OracleMesTaskUnionRoundSource : IMesTaskUnionRoundSource
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private static readonly string[] RequiredColumns =
    [
        "TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE",
    ];

    private readonly OracleSnapshotOptions _options;
    private readonly IOracleStatementExecutor? _executor;
    private readonly IOracleStatementExecutorFactory _executorFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _pollTraceIdFactory;
    private OracleExecutorIdentity? _executorIdentity;
    private IOracleStatementExecutor? _selectedExecutor;

    public OracleMesTaskUnionRoundSource(
        OracleSnapshotOptions options,
        IOracleStatementExecutor? executor = null,
        TimeProvider? timeProvider = null,
        Func<string>? pollTraceIdFactory = null,
        IOracleStatementExecutorFactory? executorFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executor = executor;
        _executorIdentity = executor?.Identity;
        _selectedExecutor = executor;
        _executorFactory = executorFactory ?? new OracleStatementExecutorFactory();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollTraceIdFactory = pollTraceIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public OracleClientMode RequestedMode => _options.Mode;

    /// <summary>The provider actually selected for the latest attempted read, if resolution succeeded.</summary>
    public OracleExecutorIdentity? ExecutorIdentity => _executorIdentity;

    /// <summary>Connection-attempt evidence for the executor selected by the latest read.</summary>
    public OracleExecutorRuntimeState ExecutorRuntimeState =>
        _selectedExecutor?.RuntimeState ?? OracleExecutorRuntimeState.Unverified;

    public async Task<MesTaskUnionRound> ReadRoundAsync(
        CancellationToken cancellationToken = default)
    {
        _executorIdentity = null;
        _selectedExecutor = null;
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = _timeProvider.GetUtcNow();
        var pollTraceId = _pollTraceIdFactory();

        CanonicalQueryArtifact artifact;
        try
        {
            artifact = CanonicalMesTaskUnionQuery.Load(_options.QuerySqlPath);
        }
        catch (CanonicalQueryArtifactException)
        {
            return Finish(
                pollTraceId,
                startedAt,
                MesTaskUnionRoundOutcome.Failure,
                [],
                new MesTaskUnionRoundDiagnostic(
                    "CANONICAL_ARTIFACT",
                    "CANONICAL_QUERY_INVALID",
                    "The approved Oracle query artifact is unavailable or invalid."));
        }

        if (_options.CommandTimeoutSeconds <= 0)
        {
            return Finish(
                pollTraceId,
                startedAt,
                MesTaskUnionRoundOutcome.Failure,
                [],
                new MesTaskUnionRoundDiagnostic(
                    "ORACLE_CONFIGURATION",
                    "ORACLE_COMMAND_TIMEOUT_INVALID",
                    "The Oracle command timeout must be a positive number of seconds."));
        }

        IOracleStatementExecutor executor;
        try
        {
            executor = _executor ?? _executorFactory.Create(_options);
            _selectedExecutor = executor;
            _executorIdentity = executor.Identity;
        }
        catch (OracleProviderConfigurationException exception)
        {
            return Finish(
                pollTraceId,
                startedAt,
                MesTaskUnionRoundOutcome.Failure,
                [],
                new MesTaskUnionRoundDiagnostic(
                    "ORACLE_CONFIGURATION",
                    exception.Code,
                    exception.Message));
        }
        catch (Exception exception)
        {
            return Finish(
                pollTraceId,
                startedAt,
                MesTaskUnionRoundOutcome.Failure,
                [],
                new MesTaskUnionRoundDiagnostic(
                    "ORACLE_CONFIGURATION",
                    "ORACLE_PROVIDER_INITIALIZATION_FAILED",
                    $"The Oracle provider could not be initialized ({exception.GetType().Name})."));
        }

        OracleStatementResult result;
        try
        {
            result = await executor.ExecuteAsync(
                new OracleStatementRequest(
                    artifact.Sql,
                    artifact.QueryVersion,
                    artifact.Sha256,
                    _options.CommandTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Some Oracle providers surface a provider exception rather than an
            // OperationCanceledException after DbCommand.Cancel. The host token
            // is authoritative, so shutdown must still propagate as cancellation.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (TimeoutException)
        {
            return ExecutionFailure(
                pollTraceId,
                startedAt,
                "ORACLE_QUERY_TIMEOUT",
                $"Oracle statement timed out after {_options.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
        catch (OracleException exception) when (exception.Number == 1013)
        {
            return ExecutionFailure(
                pollTraceId,
                startedAt,
                "ORACLE_QUERY_TIMEOUT",
                $"Oracle statement timed out after {_options.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
        catch (OdbcException exception) when (IsOdbcTimeout(exception))
        {
            return ExecutionFailure(
                pollTraceId,
                startedAt,
                "ORACLE_QUERY_TIMEOUT",
                $"Oracle statement timed out after {_options.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
        }
        catch (OperationCanceledException)
        {
            return ExecutionFailure(
                pollTraceId,
                startedAt,
                "ORACLE_QUERY_CANCELLED",
                "The Oracle provider cancelled the statement.");
        }
        catch (Exception exception)
        {
            return ExecutionFailure(
                pollTraceId,
                startedAt,
                "ORACLE_QUERY_EXECUTION_FAILED",
                $"Oracle statement failed ({exception.GetType().Name}).");
        }

        if (!TryMap(result, out var observations))
        {
            return Finish(
                pollTraceId,
                startedAt,
                MesTaskUnionRoundOutcome.Incomplete,
                [],
                new MesTaskUnionRoundDiagnostic(
                    "RESULT_MAPPING",
                    "ORACLE_RESULT_STRUCTURE_INVALID",
                    "result shape did not match the required contract."));
        }

        return Finish(
            pollTraceId,
            startedAt,
            MesTaskUnionRoundOutcome.Success,
            observations,
            diagnostic: null);
    }

    private MesTaskUnionRound ExecutionFailure(
        string pollTraceId,
        DateTimeOffset startedAt,
        string code,
        string safeDetail) =>
        Finish(
            pollTraceId,
            startedAt,
            MesTaskUnionRoundOutcome.Failure,
            [],
            new MesTaskUnionRoundDiagnostic("ORACLE_EXECUTION", code, safeDetail));

    private static bool IsOdbcTimeout(OdbcException exception) =>
        exception.Errors.Cast<OdbcError>().Any(error =>
            error.SQLState is "HYT00" or "HYT01" or "S1T00");

    private MesTaskUnionRound Finish(
        string pollTraceId,
        DateTimeOffset startedAt,
        MesTaskUnionRoundOutcome outcome,
        IReadOnlyList<MesTaskUnionObservation> observations,
        MesTaskUnionRoundDiagnostic? diagnostic) =>
        new(
            pollTraceId,
            CanonicalMesTaskUnionQuery.QueryVersion,
            outcome,
            startedAt,
            _timeProvider.GetUtcNow(),
            observations,
            diagnostic);

    private static bool TryMap(
        OracleStatementResult? result,
        out IReadOnlyList<MesTaskUnionObservation> observations)
    {
        observations = [];
        if (result?.Columns is null || result.Rows is null)
        {
            return false;
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var ordinal = 0; ordinal < result.Columns.Count; ordinal++)
        {
            var column = result.Columns[ordinal];
            if (string.IsNullOrWhiteSpace(column.Name)
                || !index.TryAdd(column.Name, ordinal))
            {
                return false;
            }
        }

        foreach (var name in RequiredColumns)
        {
            if (!index.TryGetValue(name, out var ordinal))
            {
                return false;
            }

            var kind = result.Columns[ordinal].Kind;
            var compatible = name == "DATES"
                ? kind is OracleColumnKind.DateTime or OracleColumnKind.DateTimeOffset
                : kind is OracleColumnKind.Text;
            if (!compatible)
            {
                return false;
            }
        }

        var mapped = new List<MesTaskUnionObservation>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            if (row is null || row.Count != result.Columns.Count)
            {
                return false;
            }

            if (!TryText(row[index["TASK_TYPE"]], out var taskType)
                || !TryText(row[index["SUBLOT"]], out var sublot)
                || !TryText(row[index["AREA"]], out var area)
                || !TryText(row[index["EQP"]], out var eqp)
                || !TryText(row[index["STEP"]], out var step)
                || !TryDate(row[index["DATES"]], out var date, out var dateRaw)
                || !TryText(row[index["PACKAGE"]], out var package))
            {
                return false;
            }

            mapped.Add(new MesTaskUnionObservation(
                taskType,
                sublot,
                area,
                eqp,
                step,
                date,
                package,
                dateRaw));
        }

        observations = mapped;
        return true;
    }

    private static bool TryText(object? value, out string? text)
    {
        if (value is null or DBNull)
        {
            text = null;
            return true;
        }

        if (value is string stringValue)
        {
            text = stringValue;
            return true;
        }

        if (value is char[] characters)
        {
            text = new string(characters);
            return true;
        }

        text = null;
        return false;
    }

    private static bool TryDate(
        object? value,
        out DateTimeOffset? date,
        out string? raw)
    {
        raw = null;
        switch (value)
        {
            case null:
            case DBNull:
                date = null;
                return true;
            case DateTimeOffset offset:
                date = offset;
                return true;
            case DateTime clock:
                date = clock.Kind == DateTimeKind.Unspecified
                    ? new DateTimeOffset(clock, BeijingOffset)
                    : new DateTimeOffset(clock);
                return true;
            case string text:
                raw = text;
                if (DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                    out var parsedOffset))
                {
                    date = parsedOffset;
                    return true;
                }

                if (DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out var parsedClock))
                {
                    date = new DateTimeOffset(
                        DateTime.SpecifyKind(parsedClock, DateTimeKind.Unspecified),
                        BeijingOffset);
                    return true;
                }

                date = null;
                return true;
            default:
                date = null;
                return false;
        }
    }
}

/// <summary>Managed ODP.NET Thin adapter. One call creates one SELECT command and one reader.</summary>
public sealed class OdpNetOracleStatementExecutor : IOracleStatementExecutor
{
    private readonly OracleSnapshotOptions _options;
    private readonly DbProviderFactory _providerFactory;
    private readonly bool _canAttestLiveOracle;
    private int _connectionAttempted;

    public OdpNetOracleStatementExecutor(
        OracleSnapshotOptions options,
        DbProviderFactory? providerFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ = BuildConnectionString(options);
        _canAttestLiveOracle = providerFactory is null;
        _providerFactory = providerFactory ?? OracleClientFactory.Instance;
        Identity = new OracleExecutorIdentity(
            options.Mode,
            OracleClientMode.Thin,
            "Oracle.ManagedDataAccess.Core");
    }

    public OracleExecutorIdentity Identity { get; }

    public OracleExecutorRuntimeState RuntimeState => OracleExecutorRuntimeState.Create(
        connectionAttempted: Volatile.Read(ref _connectionAttempted) != 0,
        canAttestLiveOracle: _canAttestLiveOracle);

    public async Task<OracleStatementResult> ExecuteAsync(
        OracleStatementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _connectionAttempted, 0);
        if (!string.Equals(
                request.QuerySha256,
                CanonicalMesTaskUnionQuery.ExpectedSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                request.QueryVersion,
                CanonicalMesTaskUnionQuery.QueryVersion,
                StringComparison.Ordinal)
            || !CanonicalMesTaskUnionQuery.IsApprovedSql(request.Sql)
            || request.CommandTimeoutSeconds <= 0)
        {
            throw new ArgumentException(
                "The Oracle statement request is not the approved canonical query contract.",
                nameof(request));
        }

        await using var connection = _providerFactory.CreateConnection()
            ?? throw new OracleProviderConfigurationException(
                "ORACLE_THIN_PROVIDER_UNAVAILABLE",
                "The Oracle Thin provider could not create a connection.");
        connection.ConnectionString = BuildConnectionString(_options);
        Interlocked.Exchange(ref _connectionAttempted, 1);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = request.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = request.CommandTimeoutSeconds;
        if (command is OracleCommand oracleCommand)
        {
            oracleCommand.BindByName = true;
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var columns = new OracleResultColumn[reader.FieldCount];
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            var providerType = reader.GetDataTypeName(ordinal);
            columns[ordinal] = new OracleResultColumn(
                reader.GetName(ordinal),
                providerType,
                Classify(providerType));
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                values[ordinal] = reader.IsDBNull(ordinal)
                    ? null
                    : NormalizeValue(reader.GetValue(ordinal), columns[ordinal].Kind);
            }

            rows.Add(values);
        }

        return new OracleStatementResult(columns, rows);
    }

    internal static OracleColumnKind Classify(string? providerTypeName)
    {
        var type = providerTypeName?.Trim().ToUpperInvariant() ?? "";
        if (type is "TIMESTAMPTZ" or "TIMESTAMPLTZ" or "TIMESTAMPWITHTIMEZONE" or "TIMESTAMPWITHLOCALTIMEZONE"
            || (type.StartsWith("TIMESTAMP", StringComparison.Ordinal)
                && type.Contains("WITH TIME ZONE", StringComparison.Ordinal))
            || (type.StartsWith("TIMESTAMP", StringComparison.Ordinal)
                && type.Contains("WITH LOCAL TIME ZONE", StringComparison.Ordinal)))
        {
            return OracleColumnKind.DateTimeOffset;
        }

        if (type is "DATE" || type.StartsWith("TIMESTAMP", StringComparison.Ordinal))
        {
            return OracleColumnKind.DateTime;
        }

        if (type.Contains("CHAR", StringComparison.Ordinal)
            || type.Contains("CLOB", StringComparison.Ordinal))
        {
            return OracleColumnKind.Text;
        }

        return OracleColumnKind.Unsupported;
    }

    internal static string BuildConnectionString(OracleSnapshotOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.User)
            || string.IsNullOrWhiteSpace(options.Password)
            || string.IsNullOrWhiteSpace(options.DataSource))
        {
            throw new OracleProviderConfigurationException(
                "ORACLE_THIN_CONNECTION_CONFIGURATION_REQUIRED",
                "Oracle Thin mode requires user, password, and data source configuration.");
        }

        var builder = new OracleConnectionStringBuilder
        {
            UserID = options.User,
            Password = options.Password,
            DataSource = options.DataSource,
            Pooling = true,
            MinPoolSize = Math.Max(0, options.MinPoolSize),
            MaxPoolSize = Math.Max(1, options.MaxPoolSize),
            ConnectionTimeout = Math.Max(1, options.ConnectTimeoutSeconds),
        };
        return builder.ConnectionString;
    }

    internal static object NormalizeValue(object value, OracleColumnKind kind)
    {
        if (kind is OracleColumnKind.Text && value is not string)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        if (kind is OracleColumnKind.DateTime)
        {
            return value switch
            {
                OracleDate oracleDate when !oracleDate.IsNull => oracleDate.Value,
                OracleTimeStamp oracleTimestamp when !oracleTimestamp.IsNull => oracleTimestamp.Value,
                _ => value,
            };
        }

        if (kind is OracleColumnKind.DateTimeOffset)
        {
            var timestampWithZone = value switch
            {
                OracleTimeStampTZ explicitTimestamp when !explicitTimestamp.IsNull =>
                    explicitTimestamp,
                OracleTimeStampLTZ localTimestamp when !localTimestamp.IsNull =>
                    localTimestamp.ToUniversalTime(),
                _ => default(OracleTimeStampTZ?),
            };
            if (timestampWithZone is { } timestamp)
            {
                var clock = DateTime.SpecifyKind(timestamp.Value, DateTimeKind.Unspecified);
                return new DateTimeOffset(clock, timestamp.GetTimeZoneOffset());
            }
        }

        return value;
    }
}
