using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class HistoryResetPolicyTests
{
    private static readonly HistoryEpoch Epoch = HistoryEpoch.FromGuid(
        Guid.Parse("33333333-3333-3333-3333-333333333333"));

    [Theory]
    [InlineData(false, true, true, true, true, HistoryResetAdministrationErrorCodes.Unauthorized)]
    [InlineData(true, false, true, true, true, HistoryResetAdministrationErrorCodes.NotLocalDatabaseHost)]
    [InlineData(true, true, false, true, true, HistoryResetAdministrationErrorCodes.WrongDatabase)]
    [InlineData(true, true, true, false, true, HistoryResetAdministrationErrorCodes.WrongHistoryEpoch)]
    [InlineData(true, true, true, true, false, HistoryResetAdministrationErrorCodes.RiskNotAccepted)]
    public void Acknowledgement_policy_rejects_each_failed_authority_identity_and_risk_precondition(
        bool authorized,
        bool local,
        bool exactDatabase,
        bool exactEpoch,
        bool acceptsRisk,
        string expectedCode)
    {
        var state = RequiredState();
        var request = new HistoryResetAcknowledgementRequest(
            exactDatabase ? state.DatabaseName : "WrongDatabase",
            exactEpoch ? Epoch : HistoryEpoch.CreateNew(),
            "operator accepts unrecoverable history loss",
            acceptsRisk
                ? HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance
                : "NO");
        var context = new LocalAdministrationContext(
            "operator",
            "DBHOST",
            local,
            authorized);

        var error = Assert.Throws<HistoryResetAdministrationException>(() =>
            HistoryResetAcknowledgementPolicy.Validate(context, state, request));

        Assert.Equal(expectedCode, error.Code);
        Assert.True(state.RequiresAcknowledgement);
    }

    [Fact]
    public void Acknowledgement_policy_refuses_a_history_that_did_not_lose_prior_identity()
    {
        var state = RequiredState() with
        {
            Status = HistoryResetStatuses.NotRequired,
        };
        var request = new HistoryResetAcknowledgementRequest(
            state.DatabaseName,
            state.HistoryEpoch,
            "planned empty database",
            HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance);

        var error = Assert.Throws<HistoryResetAdministrationException>(() =>
            HistoryResetAcknowledgementPolicy.Validate(AuthorizedContext(), state, request));

        Assert.Equal(HistoryResetAdministrationErrorCodes.NotRequired, error.Code);
    }

    [Fact]
    public void Repeating_the_same_acknowledgement_is_an_allowed_idempotent_request()
    {
        var acknowledged = RequiredState() with
        {
            Status = HistoryResetStatuses.Acknowledged,
            AcknowledgementAuditId = "history-reset-audit-1",
        };
        var request = new HistoryResetAcknowledgementRequest(
            acknowledged.DatabaseName,
            acknowledged.HistoryEpoch,
            "operator accepts unrecoverable history loss",
            HistoryResetAcknowledgementPolicy.RequiredRiskAcceptance);

        HistoryResetAcknowledgementPolicy.Validate(
            AuthorizedContext(),
            acknowledged,
            request);

        Assert.False(acknowledged.RequiresAcknowledgement);
    }

    private static HistoryResetStateSnapshot RequiredState() => new(
        HistoryResetStatuses.AcknowledgementRequired,
        Epoch,
        "MesIngest",
        new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
        AcknowledgementAuditId: null);

    private static LocalAdministrationContext AuthorizedContext() => new(
        @"FACTORY\authorized-operator",
        "DBHOST",
        IsLocalDatabaseHost: true,
        IsAuthorized: true);
}
