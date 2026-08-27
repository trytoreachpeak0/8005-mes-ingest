using System.Globalization;
using System.Data.Odbc;
using MesIngest.Core;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace MesIngest.Host;

internal enum SublotBoxCountReadOutcome
{
    Success,
    InvalidResult,
    Unavailable,
    Timeout,
}

internal sealed record SublotBoxCountReadResult(
    SublotBoxCountReadOutcome Outcome,
    int? MaxBoxCount,
    DateTimeOffset ObservedAt,
    string ErrorCode,
    string Error)
{
    public static SublotBoxCountReadResult Success(int maxBoxCount, DateTimeOffset observedAt) =>
        new(SublotBoxCountReadOutcome.Success, maxBoxCount, observedAt, "", "");

    public static SublotBoxCountReadResult Failure(
        SublotBoxCountReadOutcome outcome,
        string code,
        string error) => new(outcome, null, default, code, error);
}

internal interface ISublotBoxCountReader
{
    Task<SublotBoxCountReadResult> ReadAsync(
        string sublot,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableSublotBoxCountReader : ISublotBoxCountReader
{
    public Task<SublotBoxCountReadResult> ReadAsync(
        string sublot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SublotBoxCountReadResult.Failure(
            SublotBoxCountReadOutcome.Unavailable,
            "SUBLOT_BOX_COUNT_SOURCE_UNAVAILABLE",
            "The production Oracle read source is unavailable."));
    }
}

internal sealed class OracleSublotBoxCountReader(
    OracleSnapshotOptions options,
    IOracleStatementExecutorFactory executorFactory,
    TimeProvider timeProvider,
    ILogger<OracleSublotBoxCountReader> logger) : ISublotBoxCountReader
{
    public async Task<SublotBoxCountReadResult> ReadAsync(
        string sublot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sublot);
        cancellationToken.ThrowIfCancellationRequested();

        CanonicalQueryArtifact artifact;
        try
        {
            artifact = CanonicalSublotBoxCountQuery.Load(options.QuerySqlPath);
        }
        catch (CanonicalQueryArtifactException exception)
        {
            logger.LogError(
                exception,
                "The canonical SUBLOT_BOX_COUNT artifact failed validation ({Failure}).",
                exception.Failure);
            return Unavailable("SUBLOT_BOX_COUNT_QUERY_INVALID");
        }

        OracleStatementResult result;
        try
        {
            var executor = executorFactory.Create(options);
            result = await executor.ExecuteAsync(
                new OracleStatementRequest(
                    artifact.Sql,
                    artifact.QueryVersion,
                    artifact.Sha256,
                    options.CommandTimeoutSeconds,
                    [new OracleBindParameter("sublot", sublot)]),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException exception)
        {
            logger.LogWarning(exception, "SUBLOT_BOX_COUNT timed out.");
            return Timeout();
        }
        catch (OracleException exception) when (exception.Number == 1013)
        {
            logger.LogWarning(exception, "SUBLOT_BOX_COUNT timed out in the Oracle Thin provider.");
            return Timeout();
        }
        catch (OdbcException exception) when (IsOdbcTimeout(exception))
        {
            logger.LogWarning(exception, "SUBLOT_BOX_COUNT timed out in the Oracle Thick provider.");
            return Timeout();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "SUBLOT_BOX_COUNT execution failed ({ExceptionType}).",
                exception.GetType().Name);
            return Unavailable("SUBLOT_BOX_COUNT_SOURCE_UNAVAILABLE");
        }

        if (result.Columns.Count != 1
            || !string.Equals(result.Columns[0].Name, "MAX_BOX_COUNT", StringComparison.OrdinalIgnoreCase)
            || result.Rows.Count != 1
            || result.Rows[0].Count != 1
            || !TryReadPositiveInt(result.Rows[0][0], out var maxBoxCount))
        {
            return SublotBoxCountReadResult.Failure(
                SublotBoxCountReadOutcome.InvalidResult,
                "SUBLOT_BOX_COUNT_NOT_AVAILABLE",
                "SUBLOT_BOX_COUNT did not return one positive integer result.");
        }

        return SublotBoxCountReadResult.Success(maxBoxCount, timeProvider.GetUtcNow());
    }

    private static SublotBoxCountReadResult Unavailable(string code) =>
        SublotBoxCountReadResult.Failure(
            SublotBoxCountReadOutcome.Unavailable,
            code,
            "The SUBLOT_BOX_COUNT source is unavailable.");

    private static SublotBoxCountReadResult Timeout() =>
        SublotBoxCountReadResult.Failure(
            SublotBoxCountReadOutcome.Timeout,
            "SUBLOT_BOX_COUNT_TIMEOUT",
            "The SUBLOT_BOX_COUNT query timed out.");

    private static bool IsOdbcTimeout(OdbcException exception) =>
        exception.Errors.Cast<OdbcError>().Any(error =>
            error.SQLState is "HYT00" or "HYT01" or "S1T00");

    private static bool TryReadPositiveInt(object? value, out int result)
    {
        result = 0;
        decimal number;
        try
        {
            number = value switch
            {
                OracleDecimal oracleDecimal when !oracleDecimal.IsNull => oracleDecimal.Value,
                null => 0,
                _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            };
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }

        if (number <= 0 || number > int.MaxValue || decimal.Truncate(number) != number)
        {
            return false;
        }

        result = decimal.ToInt32(number);
        return true;
    }
}
