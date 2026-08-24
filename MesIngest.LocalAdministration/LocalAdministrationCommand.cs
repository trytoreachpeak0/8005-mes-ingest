using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.Infrastructure.SqlServer;

namespace MesIngest.LocalAdministration;

public static class LocalAdministrationCommand
{
    public const string DefaultConnectionStringEnvironment =
        "MES_INGEST_LOCAL_ADMINISTRATION_CONNECTION_STRING";

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        Func<string, IMesIngestLocalAdministration>? administrationFactory = null,
        Func<string, string?>? environmentReader = null)
    {
        administrationFactory ??= connectionString => new SqlServerMesIngestProjection(connectionString);
        environmentReader ??= Environment.GetEnvironmentVariable;
        if (args.Length == 0
            || args[0] is not ("resume-storage-pressure" or "acknowledge-history-reset"))
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var command = args[0];
            var values = ParseExactOptions(args[1..], command);
            var databaseName = Required(values, "--database");
            var reason = Required(values, "--reason");
            if (!Guid.TryParse(Required(values, "--history-epoch"), out var epoch)
                || epoch == Guid.Empty)
            {
                throw new ArgumentException("--history-epoch must be one non-empty GUID.");
            }

            var environmentName = values.GetValueOrDefault(
                "--connection-string-environment",
                DefaultConnectionStringEnvironment);
            if (!environmentName.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException(
                    "--connection-string-environment must be one exact environment variable name.");
            }

            var connectionString = environmentReader(environmentName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{environmentName}' is missing or empty.");
            }

            var administration = administrationFactory(connectionString);
            if (string.Equals(command, "resume-storage-pressure", StringComparison.Ordinal))
            {
                var state = await administration.ResumeStoragePressureAsync(
                    new StoragePressureRecoveryRequest(
                        databaseName,
                        HistoryEpoch.FromGuid(epoch),
                        reason)).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    result = "RECOVERED",
                    databaseName = state.DatabaseName,
                    historyEpoch = state.HistoryEpoch.Value.ToString("D"),
                    status = state.Status,
                    recoveryAuditId = state.RecoveryAuditId,
                    observedAt = state.ObservedAt,
                })).ConfigureAwait(false);
            }
            else
            {
                var state = await administration.AcknowledgeHistoryResetAsync(
                    new HistoryResetAcknowledgementRequest(
                        databaseName,
                        HistoryEpoch.FromGuid(epoch),
                        reason,
                        Required(values, "--risk-acceptance"))).ConfigureAwait(false);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    result = "ACKNOWLEDGED",
                    databaseName = state.DatabaseName,
                    historyEpoch = state.HistoryEpoch.Value.ToString("D"),
                    status = state.Status,
                    acknowledgementAuditId = state.AcknowledgementAuditId,
                    historyEpochEstablishedAt = state.HistoryEpochEstablishedAt,
                })).ConfigureAwait(false);
            }
            return 0;
        }
        catch (StoragePressureAdministrationException exception)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                code = exception.Code,
                message = exception.Message,
            })).ConfigureAwait(false);
            return 3;
        }
        catch (HistoryResetAdministrationException exception)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                code = exception.Code,
                message = exception.Message,
            })).ConfigureAwait(false);
            return 3;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new
            {
                code = "LOCAL_ADMINISTRATION_FAILED",
                message = exception.GetType().Name,
            })).ConfigureAwait(false);
            return 4;
        }
    }

    private static Dictionary<string, string> ParseExactOptions(
        string[] optionArgs,
        string command)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "--database", "--history-epoch", "--reason", "--connection-string-environment",
        };
        if (string.Equals(command, "acknowledge-history-reset", StringComparison.Ordinal))
        {
            allowed.Add("--risk-acceptance");
        }
        if (optionArgs.Length % 2 != 0)
        {
            throw new ArgumentException("Every option must have exactly one value.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < optionArgs.Length; index += 2)
        {
            var name = optionArgs[index];
            if (!allowed.Contains(name) || !values.TryAdd(name, optionArgs[index + 1]))
            {
                throw new ArgumentException($"Unsupported or repeated option '{name}'.");
            }
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{name} is required.");

    private static Task WriteUsageAsync(TextWriter error) => error.WriteLineAsync(
        "Usage: MesIngest.LocalAdministration resume-storage-pressure "
        + "--database <exact-name> --history-epoch <guid> --reason <text> "
        + "[--connection-string-environment <name>]"
        + Environment.NewLine
        + "   or: MesIngest.LocalAdministration acknowledge-history-reset "
        + "--database <exact-name> --history-epoch <guid> --reason <text> "
        + "--risk-acceptance "
        + HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance
        + " [--connection-string-environment <name>]");
}
