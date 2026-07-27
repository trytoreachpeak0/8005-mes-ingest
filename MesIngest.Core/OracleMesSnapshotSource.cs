using System.Globalization;
using System.Text;
using Oracle.ManagedDataAccess.Client;

namespace MesIngest.Core;

public enum OracleClientMode
{
    Thin,
    Thick,
}

/// <summary>
/// Connection and SQL path settings for the production Oracle MES snapshot source.
/// Credentials belong in local config / env only — never commit secrets.
/// </summary>
public sealed class OracleSnapshotOptions
{
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string DataSource { get; set; } = "";
    public OracleClientMode Mode { get; set; } = OracleClientMode.Thin;
    public string InstantClientDir { get; set; } = "";
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>ODP.NET command timeout in seconds (statement-level).</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    public int MinPoolSize { get; set; }
    public int MaxPoolSize { get; set; } = 4;

    /// <summary>Absolute or relative path to the published mes-task-union query.sql.</summary>
    public string QuerySqlPath { get; set; } = "";
}

public interface IOracleQueryExecutor
{
    Task<OracleQueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class OracleQueryResult
{
    public OracleQueryResult(
        IReadOnlyList<string> columnNames,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        ColumnNames = columnNames;
        Rows = rows;
    }

    public IReadOnlyList<string> ColumnNames { get; }
    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }
}

/// <summary>
/// Production MesSnapshotSource: runs the published MES_TASK_UNION SQL against Oracle.
/// Default mode is Thin (managed ODP.NET). Thick prepends Instant Client to PATH for plant 11g setups.
/// </summary>
public sealed class OracleMesSnapshotSource : IMesSnapshotSource
{
    private static readonly string[] RequiredColumns =
    [
        "TASK_TYPE", "SUBLOT", "AREA", "EQP", "STEP", "DATES", "PACKAGE",
    ];

    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private static readonly object ThickInitGate = new();
    private static bool _thickPathApplied;

    private readonly OracleSnapshotOptions _options;
    private readonly IOracleQueryExecutor _executor;

    public OracleMesSnapshotSource(OracleSnapshotOptions options, IOracleQueryExecutor? executor = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _executor = executor ?? new OdpNetOracleQueryExecutor(options);
    }

    public OracleClientMode RequestedMode => _options.Mode;

    /// <summary>
    /// ODP.NET Core is always managed. Thick mode only ensures Instant Client is on PATH
    /// (plant 11g / TNS practice); it does not switch to an unmanaged OCI driver.
    /// </summary>
    public bool InstantClientOnPath =>
        _options.Mode == OracleClientMode.Thick && _thickPathApplied;

    /// <summary>Apply Thick Instant Client PATH setup before probing or reading.</summary>
    public void EnsureInitialized() => EnsureClientMode();

    public async Task<MesSnapshotOutcome> ReadAsync(CancellationToken cancellationToken = default)
    {
        EnsureClientMode();

        if (string.IsNullOrWhiteSpace(_options.QuerySqlPath))
        {
            throw new InvalidOperationException("Oracle QuerySqlPath is required.");
        }

        var sql = await File.ReadAllTextAsync(_options.QuerySqlPath, Encoding.UTF8, cancellationToken);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidDataException($"Oracle query file is empty: {_options.QuerySqlPath}");
        }

        var result = await _executor.ExecuteAsync(sql, cancellationToken);
        if (!TryBuildColumnIndex(result.ColumnNames, out var index, out _))
        {
            return MesSnapshotOutcome.Incomplete();
        }

        var rows = new List<MesSnapshotRow>(result.Rows.Count);
        foreach (var raw in result.Rows)
        {
            rows.Add(MapRow(raw, index));
        }

        return MesSnapshotOutcome.Success(rows);
    }

    private void EnsureClientMode()
    {
        if (_options.Mode != OracleClientMode.Thick)
        {
            return;
        }

        lock (ThickInitGate)
        {
            if (_thickPathApplied)
            {
                return;
            }

            var clientDir = ResolveInstantClientDir();
            if (string.IsNullOrWhiteSpace(clientDir))
            {
                throw new InvalidOperationException(
                    "Oracle Thick mode requires InstantClientDir or ORACLE_CLIENT_LIB_DIR.");
            }

            if (!Directory.Exists(clientDir))
            {
                throw new DirectoryNotFoundException(
                    $"Oracle Instant Client directory not found: {clientDir}");
            }

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Any(p => string.Equals(p, clientDir, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable(
                    "PATH",
                    clientDir + Path.PathSeparator + path);
            }

            _thickPathApplied = true;
        }
    }

    private string ResolveInstantClientDir()
    {
        if (!string.IsNullOrWhiteSpace(_options.InstantClientDir))
        {
            return _options.InstantClientDir.Trim();
        }

        return (Environment.GetEnvironmentVariable("ORACLE_CLIENT_LIB_DIR") ?? "").Trim();
    }

    private static bool TryBuildColumnIndex(
        IReadOnlyList<string> columnNames,
        out Dictionary<string, int> index,
        out string? missing)
    {
        index = columnNames
            .Select((name, i) => (name, i))
            .ToDictionary(x => x.name, x => x.i, StringComparer.OrdinalIgnoreCase);

        foreach (var required in RequiredColumns)
        {
            if (!index.ContainsKey(required))
            {
                missing = required;
                return false;
            }
        }

        missing = null;
        return true;
    }

    private static MesSnapshotRow MapRow(IReadOnlyList<object?> raw, Dictionary<string, int> index) =>
        new(
            TaskType: RequireString(raw, index, "TASK_TYPE"),
            Sublot: RequireString(raw, index, "SUBLOT"),
            Area: OptionalString(raw, index, "AREA"),
            Eqp: OptionalString(raw, index, "EQP"),
            Step: OptionalString(raw, index, "STEP"),
            Dates: ParseDates(raw[index["DATES"]]),
            Package: OptionalString(raw, index, "PACKAGE"));

    private static string RequireString(
        IReadOnlyList<object?> raw,
        Dictionary<string, int> index,
        string column)
    {
        var value = OptionalString(raw, index, column);
        if (value is null)
        {
            throw new InvalidDataException($"Oracle column '{column}' is required but was null/empty.");
        }

        return value;
    }

    private static string? OptionalString(
        IReadOnlyList<object?> raw,
        Dictionary<string, int> index,
        string column)
    {
        var value = raw[index[column]];
        if (value is null || value is DBNull)
        {
            return null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static DateTimeOffset ParseDates(object? value)
    {
        if (value is null || value is DBNull)
        {
            throw new InvalidDataException("Oracle column 'DATES' is required but was null.");
        }

        if (value is DateTimeOffset dto)
        {
            return dto;
        }

        if (value is DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
            {
                return new DateTimeOffset(dt, BeijingOffset);
            }

            return new DateTimeOffset(dt);
        }

        var raw = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
                out var parsedOffset))
        {
            return parsedOffset;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var clock))
        {
            var unspecified = DateTime.SpecifyKind(clock, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, BeijingOffset);
        }

        throw new InvalidDataException($"Cannot parse DATES value '{raw}'.");
    }
}

/// <summary>
/// ODP.NET-backed executor: read-only transaction intent, then MES_TASK_UNION, always rollback/close.
/// </summary>
public sealed class OdpNetOracleQueryExecutor : IOracleQueryExecutor
{
    private readonly OracleSnapshotOptions _options;

    public OdpNetOracleQueryExecutor(OracleSnapshotOptions options)
    {
        _options = options;
    }

    public async Task<OracleQueryResult> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new OracleConnection(BuildConnectionString(_options));
        await connection.OpenAsync(cancellationToken);

        await using (var readOnly = connection.CreateCommand())
        {
            readOnly.CommandText = "SET TRANSACTION READ ONLY";
            readOnly.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.BindByName = true;
            command.CommandTimeout = Math.Max(1, _options.CommandTimeoutSeconds);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columnNames = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columnNames[i] = reader.GetName(i);
            }

            var rows = new List<IReadOnlyList<object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(values);
            }

            return new OracleQueryResult(columnNames, rows);
        }
        finally
        {
            try
            {
                // ODP.NET Core exposes sync Rollback only.
                connection.Rollback();
            }
            catch (Exception)
            {
                // Best-effort rollback on close path.
            }
        }
    }

    internal static string BuildConnectionString(OracleSnapshotOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.User)
            || string.IsNullOrWhiteSpace(options.DataSource))
        {
            throw new InvalidOperationException(
                "Oracle User and DataSource are required for Oracle snapshot mode.");
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
}
