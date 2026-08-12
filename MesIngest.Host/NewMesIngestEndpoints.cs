using MesIngest.Core.SeriesProjection;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MesIngest.Host;

internal static class NewMesIngestEndpoints
{
    public static IEndpointRouteBuilder MapNewMesIngestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v2/contract", GetContract)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/demand-series/by-key", GetDemandSeriesByKeyAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/demand-series/{seriesId}", GetDemandSeriesAsync)
            .ExcludeFromDescription();

        endpoints.MapGet("/api/v2/poll-traces/{pollTraceId}", GetPollTraceAsync)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static Ok<NewMesIngestContractDto> GetContract() =>
        TypedResults.Ok(new NewMesIngestContractDto(
            NewMesIngestContract.Version,
            NewMesIngestContract.SchemaVersion,
            NewMesIngestContract.KeyComparison,
            SeriesErrorCatalog.Definitions.Select(SeriesErrorDefinitionDto.From).ToArray()));

    private static async Task<Results<Ok<DemandSeriesDto>, BadRequest<NewMesIngestErrorDto>, NotFound>> GetDemandSeriesByKeyAsync(
        string workType,
        string sublot,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(workType, 128, nameof(workType), out var workTypeError))
        {
            return TypedResults.BadRequest(workTypeError);
        }
        if (!TryValidateRequiredText(sublot, 256, nameof(sublot), out var sublotError))
        {
            return TypedResults.BadRequest(sublotError);
        }

        var snapshot = await projection.GetDemandSeriesByKeyAsync(
            workType,
            sublot,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(DemandSeriesDto.From(snapshot));
    }

    private static async Task<Results<Ok<DemandSeriesDto>, BadRequest<NewMesIngestErrorDto>, NotFound>> GetDemandSeriesAsync(
        string seriesId,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(seriesId, 64, nameof(seriesId), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetDemandSeriesAsync(
            seriesId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(DemandSeriesDto.From(snapshot));
    }

    private static async Task<Results<Ok<PollTraceDto>, BadRequest<NewMesIngestErrorDto>, NotFound>> GetPollTraceAsync(
        string pollTraceId,
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(pollTraceId, 128, nameof(pollTraceId), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetPollTraceAsync(
            pollTraceId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(PollTraceDto.From(snapshot));
    }

    private static bool TryValidateRequiredText(
        string value,
        int maximumLength,
        string field,
        out NewMesIngestErrorDto error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = new NewMesIngestErrorDto(
                "INVALID_REQUEST",
                $"{field} must contain a non-whitespace value.");
            return false;
        }
        if (value.Length > maximumLength)
        {
            error = new NewMesIngestErrorDto(
                "INVALID_REQUEST",
                $"{field} must not exceed {maximumLength} characters.");
            return false;
        }

        error = null!;
        return true;
    }

}

internal sealed record NewMesIngestErrorDto(string Code, string Error);

internal sealed record NewMesIngestContractDto(
    string ContractVersion,
    int SchemaVersion,
    string TransportDemandKeyComparison,
    IReadOnlyList<SeriesErrorDefinitionDto> SeriesErrorCatalog);

internal sealed record SeriesErrorDefinitionDto(
    string Code,
    string Category,
    string Severity,
    string Scope,
    string Meaning)
{
    public static SeriesErrorDefinitionDto From(SeriesErrorDefinition definition) =>
        new(definition.Code, definition.Category, definition.Severity, definition.Scope, definition.Meaning);
}

internal sealed record ProjectionCommitDto(
    string ProjectionCommitId,
    string PollTraceId,
    DateTimeOffset CommittedAt)
{
    public static ProjectionCommitDto From(ProjectionCommitSnapshot snapshot) =>
        new(snapshot.ProjectionCommitId, snapshot.PollTraceId, snapshot.CommittedAt);
}

internal sealed record LiveMesFieldSetDto(
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package)
{
    public static LiveMesFieldSetDto From(LiveMesFieldSetSnapshot snapshot) =>
        new(snapshot.Area, snapshot.Eqp, snapshot.Step, snapshot.MesSourceDate, snapshot.Package);
}

internal sealed record TransportDemandV2Dto(
    string DemandId,
    string SeriesId,
    int Generation,
    string? PredecessorDemandId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset DemandLastSeenAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    LiveMesFieldSetDto LiveMesFields,
    string ExternalReadabilityState,
    IReadOnlyList<string> ReadabilityBlockers)
{
    public static TransportDemandV2Dto From(TransportDemandSnapshot snapshot) =>
        new(
            snapshot.DemandId,
            snapshot.SeriesId,
            snapshot.Generation,
            snapshot.PredecessorDemandId,
            snapshot.Status,
            snapshot.CreatedAt,
            snapshot.DemandLastSeenAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            LiveMesFieldSetDto.From(snapshot.LiveMesFields),
            snapshot.ExternalReadabilityState,
            snapshot.ReadabilityBlockers);
}

internal sealed record DemandRawObservationDto(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    string Assignment,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package)
{
    public static DemandRawObservationDto From(DemandRawObservationSnapshot snapshot) =>
        new(
            snapshot.Ordinal,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.Assignment switch
            {
                MesObservationAssignment.Assigned => "ASSIGNED",
                MesObservationAssignment.Unassigned => "UNASSIGNED",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(snapshot),
                    snapshot.Assignment,
                    "Unknown MES observation assignment."),
            },
            snapshot.SeriesId,
            snapshot.DemandId,
            snapshot.WorkType,
            snapshot.Sublot,
            snapshot.Area,
            snapshot.Eqp,
            snapshot.Step,
            snapshot.MesSourceDate,
            snapshot.Package);
}

internal sealed record DemandSeriesEventDto(
    string EventId,
    string SeriesId,
    long SeriesSequence,
    string EventType,
    DateTimeOffset OccurredAt,
    string SubjectKind,
    string? SubjectId,
    string PollTraceId,
    string ProjectionCommitId,
    int PayloadVersion,
    string PayloadJson)
{
    public static DemandSeriesEventDto From(DemandSeriesEventSnapshot snapshot) =>
        new(
            snapshot.EventId,
            snapshot.SeriesId,
            snapshot.SeriesSequence,
            snapshot.EventType,
            snapshot.OccurredAt,
            snapshot.SubjectKind,
            snapshot.SubjectId,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.PayloadVersion,
            snapshot.PayloadJson);
}

internal sealed record SeriesErrorPeriodEvidenceDto(
    string EvidenceId,
    string EvidenceKind,
    DateTimeOffset ObservedAt,
    string PollTraceId,
    string ProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule)
{
    public static SeriesErrorPeriodEvidenceDto From(SeriesErrorPeriodEvidenceSnapshot snapshot) =>
        new(
            snapshot.EvidenceId,
            snapshot.EvidenceKind,
            snapshot.ObservedAt,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.DemandId,
            snapshot.ObservedValue,
            snapshot.ExpectedRule);
}

internal sealed record DemandSeriesCurrentConditionDto(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    DateTimeOffset StartedAt,
    DateTimeOffset LatestEvidenceAt,
    string LatestPollTraceId,
    string LatestProjectionCommitId,
    string DemandId,
    string? ObservedValue,
    string ExpectedRule)
{
    public static DemandSeriesCurrentConditionDto From(DemandSeriesCurrentConditionSnapshot snapshot) =>
        new(
            snapshot.PeriodId,
            snapshot.Code,
            snapshot.Category,
            snapshot.Severity,
            snapshot.Target,
            snapshot.SubjectKind,
            snapshot.StartedAt,
            snapshot.LatestEvidenceAt,
            snapshot.LatestPollTraceId,
            snapshot.LatestProjectionCommitId,
            snapshot.DemandId,
            snapshot.ObservedValue,
            snapshot.ExpectedRule);
}

internal sealed record DemandSeriesErrorPeriodDto(
    string PeriodId,
    string Code,
    string Category,
    string Severity,
    string Target,
    string SubjectKind,
    string StartReason,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason,
    IReadOnlyList<SeriesErrorPeriodEvidenceDto> Evidence)
{
    public static DemandSeriesErrorPeriodDto From(DemandSeriesErrorPeriodSnapshot snapshot) =>
        new(
            snapshot.PeriodId,
            snapshot.Code,
            snapshot.Category,
            snapshot.Severity,
            snapshot.Target,
            snapshot.SubjectKind,
            snapshot.StartReason,
            snapshot.StartedAt,
            snapshot.EndedAt,
            snapshot.EndReason,
            snapshot.Evidence.Select(SeriesErrorPeriodEvidenceDto.From).ToArray());
}

internal sealed record DemandSeriesDto(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    DateTimeOffset StartedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    TransportDemandV2Dto CurrentDemand,
    IReadOnlyList<DemandRawObservationDto> RawObservations,
    IReadOnlyList<DemandSeriesEventDto> Events,
    IReadOnlyList<DemandSeriesCurrentConditionDto> CurrentConditions,
    IReadOnlyList<DemandSeriesErrorPeriodDto> ErrorPeriods)
{
    public static DemandSeriesDto From(DemandSeriesSnapshot snapshot) =>
        new(
            snapshot.SeriesId,
            snapshot.WorkType,
            snapshot.Sublot,
            snapshot.Lifecycle,
            snapshot.CurrentPresence,
            snapshot.StartedAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            TransportDemandV2Dto.From(snapshot.CurrentDemand),
            snapshot.RawObservations.Select(DemandRawObservationDto.From).ToList(),
            snapshot.Events.Select(DemandSeriesEventDto.From).ToList(),
            snapshot.CurrentConditions.Select(DemandSeriesCurrentConditionDto.From).ToList(),
            snapshot.ErrorPeriods.Select(DemandSeriesErrorPeriodDto.From).ToList());
}

internal sealed record PollTraceDto(
    string PollTraceId,
    string QueryVersion,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int RowCount,
    string ContentDigest,
    ProjectionCommitDto? ProjectionCommit,
    IReadOnlyList<DemandRawObservationDto> Observations)
{
    public static PollTraceDto From(PollTraceSnapshot snapshot) =>
        new(
            snapshot.PollTraceId,
            snapshot.QueryVersion,
            snapshot.Outcome,
            snapshot.StartedAt,
            snapshot.CompletedAt,
            snapshot.RowCount,
            snapshot.ContentDigest,
            snapshot.ProjectionCommit is null
                ? null
                : ProjectionCommitDto.From(snapshot.ProjectionCommit),
            snapshot.Observations.Select(DemandRawObservationDto.From).ToList());
}
