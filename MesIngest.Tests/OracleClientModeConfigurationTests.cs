using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Diagnostics.CodeAnalysis;
using MesIngest.Core;
using MesIngest.Core.SeriesProjection;
using MesIngest.Host;
using Oracle.ManagedDataAccess.Types;

namespace MesIngest.Tests;

public sealed class OracleClientModeConfigurationTests
{
    [Fact]
    public void Thin_is_the_default_and_selects_the_managed_ODP_NET_adapter()
    {
        var options = new OracleSnapshotOptions
        {
            User = "MES_READONLY",
            Password = "configured-outside-source-control",
            DataSource = "mes-service",
        };

        var executor = new OracleStatementExecutorFactory().Create(options);

        Assert.Equal(OracleClientMode.Thin, options.Mode);
        Assert.IsType<OdpNetOracleStatementExecutor>(executor);
        Assert.Equal(
            new OracleExecutorIdentity(
                OracleClientMode.Thin,
                OracleClientMode.Thin,
                "Oracle.ManagedDataAccess.Core"),
            executor.Identity);
    }

    [Fact]
    public void Thick_selects_a_distinct_OCI_ODBC_adapter_without_opening_a_connection()
    {
        using var directory = TemporaryDirectory.Create();
        var options = ValidThickOptions(directory.Path);

        var executor = new OracleStatementExecutorFactory().Create(options);

        Assert.IsType<OdbcOracleStatementExecutor>(executor);
        Assert.Equal(OracleClientMode.Thick, executor.Identity.RequestedMode);
        Assert.Equal(OracleClientMode.Thick, executor.Identity.ActualMode);
        Assert.Equal("System.Data.Odbc (Oracle OCI/Instant Client)", executor.Identity.Driver);
    }

    [Theory]
    [InlineData("directory-required", "ORACLE_INSTANT_CLIENT_DIR_REQUIRED")]
    [InlineData("directory-not-found", "ORACLE_INSTANT_CLIENT_DIR_NOT_FOUND")]
    [InlineData("driver-required", "ORACLE_ODBC_DRIVER_REQUIRED")]
    [InlineData("driver-invalid", "ORACLE_ODBC_DRIVER_INVALID")]
    [InlineData("user-required", "ORACLE_USER_REQUIRED")]
    [InlineData("password-required", "ORACLE_PASSWORD_REQUIRED")]
    [InlineData("datasource-required", "ORACLE_DATA_SOURCE_REQUIRED")]
    public void Invalid_Thick_configuration_has_a_stable_diagnostic_and_never_falls_back_to_Thin(
        string defect,
        string expectedCode)
    {
        using var directory = TemporaryDirectory.Create();
        var options = ValidThickOptions(directory.Path);
        switch (defect)
        {
            case "directory-required":
                options.InstantClientDir = "";
                break;
            case "directory-not-found":
                options.InstantClientDir = System.IO.Path.Combine(directory.Path, "missing");
                break;
            case "driver-required":
                options.ThickOdbcDriver = "";
                break;
            case "driver-invalid":
                options.ThickOdbcDriver = "Oracle;Pwd=leak";
                break;
            case "user-required":
                options.User = "";
                break;
            case "password-required":
                options.Password = "";
                break;
            case "datasource-required":
                options.DataSource = "";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }

        var exception = Assert.Throws<OracleProviderConfigurationException>(
            () => new OracleStatementExecutorFactory().Create(options));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("Pwd=leak", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("plant-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mes-11g", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_Thick_configuration_is_traceable_FAILURE_and_never_runs_a_different_provider()
    {
        var options = new OracleSnapshotOptions
        {
            Mode = OracleClientMode.Thick,
            QuerySqlPath = QueryPath(),
            InstantClientDir = "",
            ThickOdbcDriver = "Oracle in instantclient_21_13",
            User = "MES_READONLY",
            Password = "plant-secret",
            DataSource = "mes-11g",
        };
        var source = new OracleMesTaskUnionRoundSource(options);

        var round = await source.ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Failure, round.Outcome);
        Assert.Empty(round.Observations);
        Assert.Equal("ORACLE_CONFIGURATION", round.Diagnostic?.Stage);
        Assert.Equal("ORACLE_INSTANT_CLIENT_DIR_REQUIRED", round.Diagnostic?.Code);
        Assert.DoesNotContain("plant-secret", round.Diagnostic?.SafeDetail ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("mes-11g", round.Diagnostic?.SafeDetail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Round_source_uses_the_injected_factory_but_a_direct_executor_has_priority()
    {
        var options = new OracleSnapshotOptions
        {
            Mode = OracleClientMode.Thick,
            QuerySqlPath = QueryPath(),
            CommandTimeoutSeconds = 9,
        };
        var factoryExecutor = new SuccessfulStatementExecutor();
        var factory = new RecordingStatementExecutorFactory(factoryExecutor);
        var viaFactory = new OracleMesTaskUnionRoundSource(
            options,
            executorFactory: factory);

        var factoryRound = await viaFactory.ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Success, factoryRound.Outcome);
        Assert.Same(options, Assert.Single(factory.Options));
        Assert.Single(factoryExecutor.Requests);

        var directExecutor = new SuccessfulStatementExecutor();
        var direct = new OracleMesTaskUnionRoundSource(
            options,
            directExecutor,
            executorFactory: new ThrowingStatementExecutorFactory());

        var directRound = await direct.ReadRoundAsync();

        Assert.Equal(MesTaskUnionRoundOutcome.Success, directRound.Outcome);
        Assert.Single(directExecutor.Requests);
    }

    [Fact]
    public async Task Thick_executor_uses_one_command_and_one_reader_with_the_requested_timeout_and_complete_mapping()
    {
        using var directory = TemporaryDirectory.Create();
        var provider = new RecordingProviderFactory();
        DbConnection? timeoutConnection = null;
        var configuredConnectTimeout = 0;
        var options = ValidThickOptions(directory.Path);
        options.ConnectTimeoutSeconds = 23;
        var executor = new OdbcOracleStatementExecutor(
            options,
            provider,
            (connection, seconds) =>
            {
                timeoutConnection = connection;
                configuredConnectTimeout = seconds;
            });
        var request = new OracleStatementRequest(
            File.ReadAllText(QueryPath()),
            CanonicalMesTaskUnionQuery.QueryVersion,
            CanonicalMesTaskUnionQuery.ExpectedSha256,
            17);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(1, provider.ConnectionCreateCount);
        Assert.Same(provider.Connection, timeoutConnection);
        Assert.Equal(23, configuredConnectTimeout);
        Assert.True(executor.RuntimeState.ConnectionAttempted);
        Assert.False(executor.RuntimeState.CanAttestLiveOracle);
        Assert.Equal(1, provider.Connection.CommandCreateCount);
        Assert.Equal(1, provider.Connection.Command.ExecuteReaderCount);
        Assert.Equal(request.Sql, provider.Connection.Command.CommandText);
        Assert.Equal(17, provider.Connection.Command.CommandTimeout);
        Assert.Equal(CommandType.Text, provider.Connection.Command.CommandType);
        Assert.Equal(
            [
                new OracleResultColumn("TASK_TYPE", "VARCHAR2", OracleColumnKind.Text),
                new OracleResultColumn("DATES", "DATE", OracleColumnKind.DateTime),
            ],
            result.Columns);
        var row = Assert.Single(result.Rows);
        Assert.Equal("TYPE-A", row[0]);
        Assert.Equal(new DateTime(2026, 8, 14, 9, 30, 0), row[1]);
    }

    [Fact]
    public async Task Thin_executor_uses_one_command_and_one_reader_with_the_same_canonical_contract()
    {
        var provider = new RecordingProviderFactory();
        var executor = new OdpNetOracleStatementExecutor(
            new OracleSnapshotOptions
            {
                Mode = OracleClientMode.Thin,
                User = "MES_READONLY",
                Password = "plant-secret",
                DataSource = "mes-11g",
            },
            provider);
        var request = new OracleStatementRequest(
            File.ReadAllText(QueryPath()),
            CanonicalMesTaskUnionQuery.QueryVersion,
            CanonicalMesTaskUnionQuery.ExpectedSha256,
            19);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(1, provider.ConnectionCreateCount);
        Assert.True(executor.RuntimeState.ConnectionAttempted);
        Assert.False(executor.RuntimeState.CanAttestLiveOracle);
        Assert.Equal(1, provider.Connection.CommandCreateCount);
        Assert.Equal(1, provider.Connection.Command.ExecuteReaderCount);
        Assert.Equal(request.Sql, provider.Connection.Command.CommandText);
        Assert.Equal(19, provider.Connection.Command.CommandTimeout);
        Assert.Equal(CommandType.Text, provider.Connection.Command.CommandType);
        Assert.Equal(2, result.Columns.Count);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Thin_provider_type_mapping_normalizes_ODP_NET_temporal_values_at_the_adapter_boundary()
    {
        var clock = new DateTime(2026, 8, 14, 9, 30, 15, DateTimeKind.Unspecified);
        var oracleDate = new OracleDate(clock);
        var oracleTimestamp = new OracleTimeStamp(clock.AddTicks(1234));
        var oracleTimestampWithZone = new OracleTimeStampTZ(clock, "+08:00");
        var oracleLocalTimestamp = new OracleTimeStampLTZ(clock);

        Assert.Equal(
            OracleColumnKind.DateTime,
            OdpNetOracleStatementExecutor.Classify("DATE"));
        Assert.Equal(
            OracleColumnKind.DateTime,
            OdpNetOracleStatementExecutor.Classify("TIMESTAMP(6)"));
        Assert.Equal(
            OracleColumnKind.DateTimeOffset,
            OdpNetOracleStatementExecutor.Classify("TIMESTAMP(6) WITH TIME ZONE"));
        Assert.Equal(
            OracleColumnKind.DateTimeOffset,
            OdpNetOracleStatementExecutor.Classify("TimeStampTZ"));
        Assert.Equal(
            OracleColumnKind.DateTimeOffset,
            OdpNetOracleStatementExecutor.Classify("TimeStampLTZ"));
        Assert.Equal(
            OracleColumnKind.DateTimeOffset,
            OdpNetOracleStatementExecutor.Classify("TIMESTAMP WITH LOCAL TIME ZONE"));
        Assert.Equal(
            oracleDate.Value,
            OdpNetOracleStatementExecutor.NormalizeValue(oracleDate, OracleColumnKind.DateTime));
        Assert.Equal(
            oracleTimestamp.Value,
            OdpNetOracleStatementExecutor.NormalizeValue(oracleTimestamp, OracleColumnKind.DateTime));
        Assert.Equal(
            new DateTimeOffset(
                DateTime.SpecifyKind(oracleTimestampWithZone.Value, DateTimeKind.Unspecified),
                oracleTimestampWithZone.GetTimeZoneOffset()),
            OdpNetOracleStatementExecutor.NormalizeValue(
                oracleTimestampWithZone,
                OracleColumnKind.DateTimeOffset));
        var localAsUtc = oracleLocalTimestamp.ToUniversalTime();
        Assert.Equal(
            new DateTimeOffset(
                DateTime.SpecifyKind(localAsUtc.Value, DateTimeKind.Unspecified),
                localAsUtc.GetTimeZoneOffset()),
            OdpNetOracleStatementExecutor.NormalizeValue(
                oracleLocalTimestamp,
                OracleColumnKind.DateTimeOffset));
    }

    [Fact]
    public void Thick_default_timeout_configurator_sets_the_real_ODBC_login_timeout_property()
    {
        using var connection = new OdbcConnection();

        OdbcOracleStatementExecutor.ApplySystemOdbcConnectionTimeoutForTest(connection, 31);

        Assert.Equal(31, connection.ConnectionTimeout);
    }

    [Fact]
    public async Task Provider_adapters_reject_any_second_or_alternate_SQL_before_opening_a_connection()
    {
        using var directory = TemporaryDirectory.Create();
        foreach (var executor in new IOracleStatementExecutor[]
        {
            new OdpNetOracleStatementExecutor(
                new OracleSnapshotOptions
                {
                    Mode = OracleClientMode.Thin,
                    User = "MES_READONLY",
                    Password = "plant-secret",
                    DataSource = "mes-11g",
                },
                new RecordingProviderFactory()),
            new OdbcOracleStatementExecutor(
                ValidThickOptions(directory.Path),
                new RecordingProviderFactory()),
        })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => executor.ExecuteAsync(
                new OracleStatementRequest(
                    "SELECT 1 FROM DUAL",
                    CanonicalMesTaskUnionQuery.QueryVersion,
                    CanonicalMesTaskUnionQuery.ExpectedSha256,
                    5)));
        }
    }

    [Fact]
    public async Task Thick_executor_propagates_cancellation_before_opening_a_connection()
    {
        using var directory = TemporaryDirectory.Create();
        var provider = new RecordingProviderFactory();
        var executor = new OdbcOracleStatementExecutor(
            ValidThickOptions(directory.Path),
            provider);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(
                new OracleStatementRequest("SELECT 1 FROM DUAL", "v", "sha", 5),
                cancellation.Token));

        Assert.Equal(0, provider.ConnectionCreateCount);
        Assert.False(executor.RuntimeState.ConnectionAttempted);
    }

    [Fact]
    public void V2_options_use_ORACLE_CLIENT_LIB_DIR_only_when_the_explicit_value_is_blank()
    {
        const string variable = "ORACLE_CLIENT_LIB_DIR";
        var previous = Environment.GetEnvironmentVariable(variable);
        var fallback = Path.Combine(Path.GetTempPath(), $"oracle-env-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(variable, $"  {fallback}  ");
            var hostOptions = new MesIngestHostOptions
            {
                OracleInstantClientDir = "",
            };

            Assert.Equal(fallback, hostOptions.ToOracleSnapshotOptions().InstantClientDir);

            var explicitDirectory = Path.Combine(Path.GetTempPath(), $"oracle-explicit-{Guid.NewGuid():N}");
            hostOptions.OracleInstantClientDir = $"  {explicitDirectory}  ";
            Assert.Equal(
                explicitDirectory,
                hostOptions.ToOracleSnapshotOptions().InstantClientDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static OracleSnapshotOptions ValidThickOptions(string instantClientDir) => new()
    {
        Mode = OracleClientMode.Thick,
        InstantClientDir = instantClientDir,
        ThickOdbcDriver = "Oracle in instantclient_21_13",
        User = "MES_READONLY",
        Password = "plant-secret",
        DataSource = "mes-11g",
    };

    private static string QueryPath() => Path.Combine(
        AppContext.BaseDirectory,
        "queries",
        "mes-task-union",
        "query.sql");

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ticket15-thick-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class RecordingProviderFactory : DbProviderFactory
    {
        public RecordingConnection Connection { get; } = new();
        public int ConnectionCreateCount { get; private set; }

        public override DbConnection CreateConnection()
        {
            ConnectionCreateCount++;
            return Connection;
        }
    }

    private sealed class RecordingConnection : DbConnection
    {
        private ConnectionState _state;

        public RecordingCommand Command { get; } = new();
        public int CommandCreateCount { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "MES";
        public override string DataSource => "mes-11g";
        public override string ServerVersion => "11.2";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) =>
            throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open() => _state = ConnectionState.Open;

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {
            CommandCreateCount++;
            Command.Connection = this;
            return Command;
        }
    }

    private sealed class RecordingCommand : DbCommand
    {
        private readonly DbParameterCollection _parameters = new EmptyParameterCollection();

        public int ExecuteReaderCount { get; private set; }
        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() =>
            throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            CreateReader();

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<DbDataReader>(CreateReader());
        }

        private DbDataReader CreateReader()
        {
            ExecuteReaderCount++;
            var table = new DataTable();
            var taskType = table.Columns.Add("TASK_TYPE", typeof(string));
            taskType.ExtendedProperties["ProviderTypeName"] = "VARCHAR2";
            var dates = table.Columns.Add("DATES", typeof(DateTime));
            dates.ExtendedProperties["ProviderTypeName"] = "DATE";
            table.Rows.Add("TYPE-A", new DateTime(2026, 8, 14, 9, 30, 0));
            return new ProviderNamedDataReader(
                table.CreateDataReader(),
                ["VARCHAR2", "DATE"]);
        }
    }

    private sealed class ProviderNamedDataReader(
        DbDataReader inner,
        IReadOnlyList<string> providerTypeNames) : DbDataReader
    {
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => providerTypeNames[ordinal];
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override bool Read() => inner.Read();
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);
        public override System.Collections.IEnumerator GetEnumerator() => ((System.Collections.IEnumerable)inner).GetEnumerator();
        public override DataTable? GetSchemaTable() => inner.GetSchemaTable();
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class EmptyParameterCollection : DbParameterCollection
    {
        private readonly List<object> _items = [];
        public override int Count => _items.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _items.Add(value); return _items.Count - 1; }
        public override void AddRange(Array values) { foreach (var value in values) { Add(value!); } }
        public override void Clear() => _items.Clear();
        public override bool Contains(object value) => _items.Contains(value);
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();
        public override int IndexOf(object value) => _items.IndexOf(value);
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object value) => _items.Insert(index, value);
        public override void Remove(object value) => _items.Remove(value);
        public override void RemoveAt(int index) => _items.RemoveAt(index);
        public override void RemoveAt(string parameterName) { }
        protected override DbParameter GetParameter(int index) => (DbParameter)_items[index];
        protected override DbParameter GetParameter(string parameterName) => throw new IndexOutOfRangeException();
        protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => throw new IndexOutOfRangeException();
    }

    private sealed class RecordingStatementExecutorFactory(IOracleStatementExecutor executor)
        : IOracleStatementExecutorFactory
    {
        public List<OracleSnapshotOptions> Options { get; } = [];

        public IOracleStatementExecutor Create(OracleSnapshotOptions options)
        {
            Options.Add(options);
            return executor;
        }
    }

    private sealed class ThrowingStatementExecutorFactory : IOracleStatementExecutorFactory
    {
        public IOracleStatementExecutor Create(OracleSnapshotOptions options) =>
            throw new InvalidOperationException("factory must not be used");
    }

    private sealed class SuccessfulStatementExecutor : IOracleStatementExecutor
    {
        public OracleExecutorIdentity Identity { get; } = new(
            OracleClientMode.Thick,
            OracleClientMode.Thick,
            "fake-thick");

        public List<OracleStatementRequest> Requests { get; } = [];

        public Task<OracleStatementResult> ExecuteAsync(
            OracleStatementRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new OracleStatementResult(
                [
                    new("TASK_TYPE", "VARCHAR2", OracleColumnKind.Text),
                    new("SUBLOT", "VARCHAR2", OracleColumnKind.Text),
                    new("AREA", "VARCHAR2", OracleColumnKind.Text),
                    new("EQP", "VARCHAR2", OracleColumnKind.Text),
                    new("STEP", "VARCHAR2", OracleColumnKind.Text),
                    new("DATES", "DATE", OracleColumnKind.DateTime),
                    new("PACKAGE", "VARCHAR2", OracleColumnKind.Text),
                ],
                []));
        }
    }
}
