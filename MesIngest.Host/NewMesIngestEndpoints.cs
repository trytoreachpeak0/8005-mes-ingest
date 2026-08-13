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

        endpoints.MapGet("/api/v2/absence-authority", GetAbsenceAuthorityAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/absence-authority/{hostSessionId}",
                GetAbsenceAuthorityByHostSessionIdAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/task-type-protections",
                ListTaskTypeProtectionsAsync)
            .ExcludeFromDescription();

        endpoints.MapGet(
                "/api/v2/task-type-protections/{workType}",
                GetTaskTypeProtectionAsync)
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

    private static async Task<Ok<AbsenceAuthorityDto>> GetAbsenceAuthorityAsync(
        IMesIngestProjection projection,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(AbsenceAuthorityDto.From(
            await projection.GetAbsenceAuthorityAsync(cancellationToken)));

    private static async Task<Results<Ok<AbsenceAuthorityDto>, BadRequest<NewMesIngestErrorDto>, NotFound>>
        GetAbsenceAuthorityByHostSessionIdAsync(
            string hostSessionId,
            IMesIngestProjection projection,
            CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(hostSessionId, 64, nameof(hostSessionId), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetAbsenceAuthorityAsync(
            hostSessionId,
            cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(AbsenceAuthorityDto.From(snapshot));
    }

    private static async Task<Ok<TaskTypeProtectionListDto>> ListTaskTypeProtectionsAsync(
        IMesIngestProjection projection,
        CancellationToken cancellationToken)
    {
        var snapshots = await projection.ListTaskTypeProtectionsAsync(cancellationToken);
        var items = snapshots
            .OrderBy(snapshot => snapshot.WorkType, StringComparer.Ordinal)
            .Select(TaskTypeProtectionDto.From)
            .ToArray();

        return TypedResults.Ok(new TaskTypeProtectionListDto(items.Length, items));
    }

    private static async Task<Results<Ok<TaskTypeProtectionDto>, BadRequest<NewMesIngestErrorDto>, NotFound>>
        GetTaskTypeProtectionAsync(
            string workType,
            IMesIngestProjection projection,
            CancellationToken cancellationToken)
    {
        if (!TryValidateRequiredText(workType, 128, nameof(workType), out var error))
        {
            return TypedResults.BadRequest(error);
        }

        var snapshot = await projection.GetTaskTypeProtectionAsync(workType, cancellationToken);
        return snapshot is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(TaskTypeProtectionDto.From(snapshot));
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
    DateTimeOffset CommittedAt,
    string HostSessionId,
    string RestartPhaseBefore,
    string RestartPhaseAfter,
    bool AbsenceAuthority,
    IReadOnlyList<TaskTypeProtectionDecisionDto> TaskTypeProtectionDecisions)
{
    public static ProjectionCommitDto From(ProjectionCommitSnapshot snapshot) =>
        new(
            snapshot.ProjectionCommitId,
            snapshot.PollTraceId,
            snapshot.CommittedAt,
            snapshot.HostSessionId,
            snapshot.RestartPhaseBefore,
            snapshot.RestartPhaseAfter,
            snapshot.AbsenceAuthority,
            snapshot.TaskTypeProtectionDecisions
                .OrderBy(decision => decision.WorkType, StringComparer.Ordinal)
                .Select(TaskTypeProtectionDecisionDto.From)
                .ToArray());
}

internal sealed record TaskTypeProtectionDecisionDto(
    string WorkType,
    string PhaseBefore,
    string PhaseAfter,
    int ObservedCount,
    int LastHealthyNonZeroCount,
    int RecoveryStreakBefore,
    int RecoveryStreakAfter,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    IReadOnlyList<string> EventIds)
{
    public static TaskTypeProtectionDecisionDto From(TaskTypeProtectionDecisionSnapshot snapshot) =>
        new(
            snapshot.WorkType,
            snapshot.PhaseBefore,
            snapshot.PhaseAfter,
            snapshot.ObservedCount,
            snapshot.LastHealthyNonZeroCount,
            snapshot.RecoveryStreakBefore,
            snapshot.RecoveryStreakAfter,
            snapshot.ProtectionAllowsAbsenceAuthority,
            snapshot.EffectiveAbsenceAuthorityAvailable,
            snapshot.EventIds);
}

internal sealed record TaskTypeProtectionEventDto(
    string EventId,
    string EpisodeId,
    string WorkType,
    long WorkTypeSequence,
    string EventType,
    DateTimeOffset OccurredAt,
    string PollTraceId,
    string ProjectionCommitId,
    string PhaseBefore,
    string PhaseAfter,
    int ObservedCount,
    int LastHealthyNonZeroCount,
    int RecoveryStreak,
    int RequiredRecoveryStreak,
    int EnterThreshold)
{
    public static TaskTypeProtectionEventDto From(TaskTypeProtectionEventSnapshot snapshot) =>
        new(
            snapshot.EventId,
            snapshot.EpisodeId,
            snapshot.WorkType,
            snapshot.WorkTypeSequence,
            snapshot.EventType,
            snapshot.OccurredAt,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.PhaseBefore,
            snapshot.PhaseAfter,
            snapshot.ObservedCount,
            snapshot.LastHealthyNonZeroCount,
            snapshot.RecoveryStreak,
            snapshot.RequiredRecoveryStreak,
            snapshot.EnterThreshold);
}

internal sealed record TaskTypeProtectionDto(
    string WorkType,
    string Phase,
    bool IsCurrentAttention,
    int LastHealthyNonZeroCount,
    int LatestObservedCount,
    int RecoveryStreak,
    int RequiredRecoveryStreak,
    int EnterThreshold,
    string? EpisodeId,
    DateTimeOffset? EnteredAt,
    bool ProtectionAllowsAbsenceAuthority,
    bool EffectiveAbsenceAuthorityAvailable,
    string LatestPollTraceId,
    string LatestProjectionCommitId,
    IReadOnlyList<TaskTypeProtectionEventDto> Events)
{
    public static TaskTypeProtectionDto From(TaskTypeProtectionSnapshot snapshot) =>
        new(
            snapshot.WorkType,
            snapshot.Phase,
            snapshot.IsCurrentAttention,
            snapshot.LastHealthyNonZeroCount,
            snapshot.LatestObservedCount,
            snapshot.RecoveryStreak,
            snapshot.RequiredRecoveryStreak,
            snapshot.EnterThreshold,
            snapshot.EpisodeId,
            snapshot.EnteredAt,
            snapshot.ProtectionAllowsAbsenceAuthority,
            snapshot.EffectiveAbsenceAuthorityAvailable,
            snapshot.LatestPollTraceId,
            snapshot.LatestProjectionCommitId,
            snapshot.Events
                .OrderBy(item => item.WorkTypeSequence)
                .Select(TaskTypeProtectionEventDto.From)
                .ToArray());
}

internal sealed record TaskTypeProtectionListDto(
    int Total,
    IReadOnlyList<TaskTypeProtectionDto> Items);

internal sealed record AbsenceAuthorityEventDto(
    string EventId,
    string HostSessionId,
    string EventType,
    DateTimeOffset OccurredAt,
    string? PollTraceId,
    string? ProjectionCommitId,
    string PhaseBefore,
    string PhaseAfter)
{
    public static AbsenceAuthorityEventDto From(AbsenceAuthorityEventSnapshot snapshot) =>
        new(
            snapshot.EventId,
            snapshot.HostSessionId,
            snapshot.EventType,
            snapshot.OccurredAt,
            snapshot.PollTraceId,
            snapshot.ProjectionCommitId,
            snapshot.PhaseBefore,
            snapshot.PhaseAfter);
}

internal sealed record AbsenceAuthorityDto(
    string HostSessionId,
    DateTimeOffset StartedAt,
    string Phase,
    bool IsCurrent,
    bool AbsenceAuthorityAvailable,
    IReadOnlyList<AbsenceAuthorityEventDto> Events)
{
    public static AbsenceAuthorityDto From(AbsenceAuthoritySnapshot snapshot) =>
        new(
            snapshot.HostSessionId,
            snapshot.StartedAt,
            snapshot.Phase,
            snapshot.IsCurrent,
            snapshot.AbsenceAuthorityAvailable,
            snapshot.Events.Select(AbsenceAuthorityEventDto.From).ToArray());
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
    DateTimeOffset? GoneConfirmedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    LiveMesFieldSetDto? LiveMesFields,
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
            snapshot.GoneConfirmedAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            snapshot.LiveMesFields is null
                ? null
                : LiveMesFieldSetDto.From(snapshot.LiveMesFields),
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
    DateTimeOffset? ArchivedAt,
    string CreatedPollTraceId,
    string CreatedProjectionCommitId,
    string LatestProjectionCommitId,
    TransportDemandV2Dto CurrentDemand,
    IReadOnlyList<TransportDemandV2Dto> Demands,
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
            snapshot.ArchivedAt,
            snapshot.CreatedPollTraceId,
            snapshot.CreatedProjectionCommitId,
            snapshot.LatestProjectionCommitId,
            TransportDemandV2Dto.From(snapshot.CurrentDemand),
            snapshot.Demands.Select(TransportDemandV2Dto.From).ToList(),
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
