namespace MesIngest.Core.SeriesProjection;

public static class HistoryResetStatuses
{
    public const string NotRequired = "NOT_REQUIRED";
    public const string AcknowledgementRequired = "ACKNOWLEDGEMENT_REQUIRED";
    public const string Acknowledged = "ACKNOWLEDGED";

    public static IReadOnlyList<string> All { get; } =
        [NotRequired, AcknowledgementRequired, Acknowledged];
}

public sealed record HistoryResetStateSnapshot(
    string Status,
    HistoryEpoch HistoryEpoch,
    string DatabaseName,
    DateTimeOffset HistoryEpochEstablishedAt,
    string? AcknowledgementAuditId,
    DateTimeOffset? AcknowledgementRequiredAt = null)
{
    public bool RequiresAcknowledgement => string.Equals(
        Status,
        HistoryResetStatuses.AcknowledgementRequired,
        StringComparison.Ordinal);
}

public sealed record HistoryResetAcknowledgementRequest(
    string DatabaseName,
    HistoryEpoch HistoryEpoch,
    string Reason,
    string RiskAcceptance);

public interface IHistoryResetOperations
{
    Task<HistoryResetStateSnapshot> ReadHistoryResetStateAsync(
        CancellationToken cancellationToken = default);
}

public static class HistoryResetAdministrationErrorCodes
{
    public const string Unauthorized = "HISTORY_RESET_ACKNOWLEDGEMENT_UNAUTHORIZED";
    public const string NotLocalDatabaseHost = "HISTORY_RESET_ACKNOWLEDGEMENT_NOT_LOCAL";
    public const string WrongDatabase = "HISTORY_RESET_ACKNOWLEDGEMENT_WRONG_DATABASE";
    public const string WrongHistoryEpoch = "HISTORY_RESET_ACKNOWLEDGEMENT_WRONG_HISTORY_EPOCH";
    public const string RiskNotAccepted = "HISTORY_RESET_ACKNOWLEDGEMENT_RISK_NOT_ACCEPTED";
    public const string NotRequired = "HISTORY_RESET_ACKNOWLEDGEMENT_NOT_REQUIRED";
}

public sealed class HistoryResetAdministrationException : Exception
{
    public HistoryResetAdministrationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class HistoryEpochMismatchException : Exception
{
    public const string ErrorCode = "HISTORY_EPOCH_MISMATCH";

    public HistoryEpochMismatchException(HistoryEpoch expected, HistoryEpoch actual)
        : base(
            $"The supplied HistoryEpoch '{actual}' does not match the current "
            + $"HistoryEpoch '{expected}'.")
    {
        CurrentHistoryEpoch = expected;
        SuppliedHistoryEpoch = actual;
    }

    public string Code => ErrorCode;

    public HistoryEpoch CurrentHistoryEpoch { get; }

    public HistoryEpoch SuppliedHistoryEpoch { get; }
}

public static class HistoryResetAcknowledgementPolicy
{
    public const string RequiredRiskAcceptance =
        "I_ACCEPT_UNRECOVERABLE_LOSS_OF_PRIOR_HISTORY_AND_TOMBSTONES";

    public static void Validate(
        LocalAdministrationContext context,
        HistoryResetStateSnapshot state,
        HistoryResetAcknowledgementRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);

        if (!context.IsAuthorized)
        {
            Throw(
                HistoryResetAdministrationErrorCodes.Unauthorized,
                "The execution identity is not authorized for local MesIngest administration.");
        }
        if (!context.IsLocalDatabaseHost)
        {
            Throw(
                HistoryResetAdministrationErrorCodes.NotLocalDatabaseHost,
                "HistoryResetAcknowledgement must run on the database host.");
        }
        if (!string.Equals(state.DatabaseName, request.DatabaseName, StringComparison.Ordinal))
        {
            Throw(
                HistoryResetAdministrationErrorCodes.WrongDatabase,
                "The requested database does not exactly match the configured database.");
        }
        if (state.HistoryEpoch != request.HistoryEpoch)
        {
            Throw(
                HistoryResetAdministrationErrorCodes.WrongHistoryEpoch,
                "The requested HistoryEpoch does not match the current database epoch.");
        }
        if (!string.Equals(
                request.RiskAcceptance,
                RequiredRiskAcceptance,
                StringComparison.Ordinal))
        {
            Throw(
                HistoryResetAdministrationErrorCodes.RiskNotAccepted,
                "The exact unrecoverable prior-history and tombstone risk must be accepted.");
        }
        if (!state.RequiresAcknowledgement
            && state.AcknowledgementAuditId is null)
        {
            Throw(
                HistoryResetAdministrationErrorCodes.NotRequired,
                "This HistoryEpoch does not require HistoryResetAcknowledgement.");
        }
    }

    private static void Throw(string code, string message) =>
        throw new HistoryResetAdministrationException(code, message);
}
