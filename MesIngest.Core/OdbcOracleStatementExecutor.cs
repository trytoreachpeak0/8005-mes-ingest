using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;

namespace MesIngest.Core;

/// <summary>
/// Oracle Thick adapter backed by the registered Oracle ODBC driver. The
/// driver loads OCI from the configured Instant Client installation; this is
/// intentionally a different provider from managed ODP.NET Thin.
/// </summary>
public sealed class OdbcOracleStatementExecutor : IOracleStatementExecutor
{
    private static readonly object InstantClientPathGate = new();

    private readonly string _connectionString;
    private readonly string _instantClientDir;
    private readonly DbProviderFactory _providerFactory;
    private readonly bool _usesSystemOdbcProvider;
    private readonly int _connectTimeoutSeconds;
    private readonly Action<DbConnection, int> _configureConnectionTimeout;
    private readonly bool _canAttestLiveOracle;
    private int _connectionAttempted;

    public OdbcOracleStatementExecutor(
        OracleSnapshotOptions options,
        DbProviderFactory? providerFactory = null,
        Action<DbConnection, int>? configureConnectionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _instantClientDir = Validate(options);

        _connectionString = BuildConnectionString(options);
        _connectTimeoutSeconds = Math.Max(1, options.ConnectTimeoutSeconds);
        _providerFactory = providerFactory ?? OdbcFactory.Instance;
        _usesSystemOdbcProvider = providerFactory is null;
        _canAttestLiveOracle = providerFactory is null && configureConnectionTimeout is null;
        _configureConnectionTimeout = configureConnectionTimeout
            ?? (_usesSystemOdbcProvider
                ? ConfigureSystemOdbcConnectionTimeout
                : static (_, _) => { });
        Identity = new OracleExecutorIdentity(
            options.Mode,
            OracleClientMode.Thick,
            "System.Data.Odbc (Oracle OCI/Instant Client)");
    }

    public OracleExecutorIdentity Identity { get; }

    public OracleExecutorRuntimeState RuntimeState => OracleExecutorRuntimeState.Create(
        connectionAttempted: Volatile.Read(ref _connectionAttempted) != 0,
        canAttestLiveOracle: _canAttestLiveOracle);

    internal static void ApplySystemOdbcConnectionTimeoutForTest(
        DbConnection connection,
        int connectTimeoutSeconds) =>
        ConfigureSystemOdbcConnectionTimeout(connection, connectTimeoutSeconds);

    public async Task<OracleStatementResult> ExecuteAsync(
        OracleStatementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Volatile.Write(ref _connectionAttempted, 0);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ApprovedOracleStatementRequest.IsValid(request))
        {
            throw new ArgumentException(
                "The Oracle statement request is not an approved canonical query contract.",
                nameof(request));
        }

        if (_usesSystemOdbcProvider)
        {
            EnsureInstantClientOnPath(_instantClientDir);
        }

        await using var connection = _providerFactory.CreateConnection()
            ?? throw new OracleProviderConfigurationException(
                "ORACLE_ODBC_PROVIDER_UNAVAILABLE",
                "The configured Oracle ODBC provider could not create a connection.");
        connection.ConnectionString = _connectionString;
        _configureConnectionTimeout(connection, _connectTimeoutSeconds);
        Interlocked.Exchange(ref _connectionAttempted, 1);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = PrepareCommandText(request);
        command.CommandType = CommandType.Text;
        command.CommandTimeout = request.CommandTimeoutSeconds;
        AddBindParameters(command, request.BindParameters);

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                try
                {
                    ((DbCommand)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // A cancellation racing command disposal needs no further action.
                }
                catch (InvalidOperationException)
                {
                    // Some ODBC drivers reject Cancel after the reader has completed.
                }
                catch (DbException)
                {
                    // Cancellation is best effort; the caller token remains authoritative.
                }
            },
            command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var columns = ReadColumns(reader);
        var rows = await ReadRowsAsync(reader, columns, cancellationToken)
            .ConfigureAwait(false);
        return new OracleStatementResult(columns, rows);
    }

    private static string PrepareCommandText(OracleStatementRequest request)
    {
        if (request.BindParameters is not { Count: > 0 })
        {
            return request.Sql;
        }

        var marker = ":sublot";
        var markerIndex = request.Sql.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new ArgumentException(
                "The approved parameterized Oracle statement is missing its bind marker.",
                nameof(request));
        }

        return string.Concat(
            request.Sql.AsSpan(0, markerIndex),
            "?",
            request.Sql.AsSpan(markerIndex + marker.Length));
    }

    private static void AddBindParameters(
        DbCommand command,
        IReadOnlyList<OracleBindParameter>? bindParameters)
    {
        foreach (var bind in bindParameters ?? [])
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = bind.Name;
            parameter.DbType = DbType.String;
            parameter.Size = CanonicalSublotBoxCountQuery.MaximumSublotLength;
            parameter.Value = bind.Value;
            command.Parameters.Add(parameter);
        }
    }

    private static void ConfigureSystemOdbcConnectionTimeout(
        DbConnection connection,
        int connectTimeoutSeconds)
    {
        if (connection is not OdbcConnection odbcConnection)
        {
            throw new OracleProviderConfigurationException(
                "ORACLE_ODBC_PROVIDER_UNAVAILABLE",
                "The configured Oracle ODBC provider did not create an ODBC connection.");
        }

        // ODBC has no supported connection-string timeout keyword. The property
        // maps to the driver's login timeout and must be assigned before Open.
        odbcConnection.ConnectionTimeout = connectTimeoutSeconds;
    }

    private static IReadOnlyList<OracleResultColumn> ReadColumns(DbDataReader reader)
    {
        var columns = new OracleResultColumn[reader.FieldCount];
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
        {
            var providerType = reader.GetDataTypeName(ordinal);
            columns[ordinal] = new OracleResultColumn(
                reader.GetName(ordinal),
                providerType,
                OdpNetOracleStatementExecutor.Classify(providerType));
        }

        return columns;
    }

    private static async Task<IReadOnlyList<IReadOnlyList<object?>>> ReadRowsAsync(
        DbDataReader reader,
        IReadOnlyList<OracleResultColumn> columns,
        CancellationToken cancellationToken)
    {
        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                values[ordinal] = await reader.IsDBNullAsync(ordinal, cancellationToken)
                    .ConfigureAwait(false)
                    ? null
                    : NormalizeValue(reader.GetValue(ordinal), columns[ordinal].Kind);
            }

            rows.Add(values);
        }

        return rows;
    }

    private static object NormalizeValue(object value, OracleColumnKind kind)
    {
        if (kind is OracleColumnKind.Text && value is not string)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        return value;
    }

    private static string BuildConnectionString(OracleSnapshotOptions options)
    {
        var builder = new OdbcConnectionStringBuilder
        {
            Driver = options.ThickOdbcDriver.Trim(),
        };
        builder["Dbq"] = options.DataSource.Trim();
        builder["Uid"] = options.User.Trim();
        builder["Pwd"] = options.Password;
        return builder.ConnectionString;
    }

    private static string Validate(OracleSnapshotOptions options)
    {
        if (options.Mode != OracleClientMode.Thick)
        {
            throw ConfigurationError(
                "ORACLE_MODE_MISMATCH",
                "The Oracle ODBC adapter requires Thick mode.");
        }

        if (string.IsNullOrWhiteSpace(options.InstantClientDir))
        {
            throw ConfigurationError(
                "ORACLE_INSTANT_CLIENT_DIR_REQUIRED",
                "Oracle Thick mode requires an Instant Client directory.");
        }

        if (!Directory.Exists(options.InstantClientDir.Trim()))
        {
            throw ConfigurationError(
                "ORACLE_INSTANT_CLIENT_DIR_NOT_FOUND",
                "The configured Oracle Instant Client directory does not exist.");
        }

        if (string.IsNullOrWhiteSpace(options.ThickOdbcDriver))
        {
            throw ConfigurationError(
                "ORACLE_ODBC_DRIVER_REQUIRED",
                "Oracle Thick mode requires a registered Oracle ODBC driver name.");
        }

        if (options.ThickOdbcDriver.IndexOfAny([';', '{', '}', '=', '\r', '\n']) >= 0)
        {
            throw ConfigurationError(
                "ORACLE_ODBC_DRIVER_INVALID",
                "The configured Oracle ODBC driver name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(options.User))
        {
            throw ConfigurationError(
                "ORACLE_USER_REQUIRED",
                "Oracle user configuration is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw ConfigurationError(
                "ORACLE_PASSWORD_REQUIRED",
                "Oracle password configuration is required.");
        }

        if (string.IsNullOrWhiteSpace(options.DataSource))
        {
            throw ConfigurationError(
                "ORACLE_DATA_SOURCE_REQUIRED",
                "Oracle data source configuration is required.");
        }

        try
        {
            return Path.GetFullPath(options.InstantClientDir.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw ConfigurationError(
                "ORACLE_INSTANT_CLIENT_DIR_INVALID",
                "The configured Oracle Instant Client directory is invalid.");
        }
    }

    private static void EnsureInstantClientOnPath(string instantClientDir)
    {
        lock (InstantClientPathGate)
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (currentPath.Split(
                    Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(segment => string.Equals(
                    segment,
                    instantClientDir,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Environment.SetEnvironmentVariable(
                "PATH",
                instantClientDir + Path.PathSeparator + currentPath);
        }
    }

    private static OracleProviderConfigurationException ConfigurationError(
        string code,
        string safeMessage) => new(code, safeMessage);
}
