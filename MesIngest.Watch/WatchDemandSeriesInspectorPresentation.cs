using System.Text.Json;
using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

internal sealed record WatchDemandSeriesInspectorStatePresentation(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    WatchDemandSeriesFrozenSnapshotPresentation FrozenSnapshot,
    WatchDemandSeriesInspectorPresentation? Detail,
    bool IsLoading,
    bool IsStale,
    bool IsPaused,
    WatchPresentationSeverity StatusSeverity,
    string StatusTitle,
    string StatusMessage)
{
    public static WatchDemandSeriesInspectorStatePresentation Loaded(
        WatchDemandSeriesInspectorPresentation detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new WatchDemandSeriesInspectorStatePresentation(
            detail.SeriesId,
            detail.WorkType,
            detail.Sublot,
            detail.Lifecycle,
            detail.CurrentPresence,
            detail.FrozenSnapshot,
            detail,
            IsLoading: false,
            IsStale: false,
            IsPaused: false,
            WatchPresentationSeverity.None,
            StatusTitle: string.Empty,
            StatusMessage: string.Empty);
    }
}

internal enum WatchDemandFormationFactKind
{
    PredecessorIdentity,
    PredecessorLastObservation,
    AuthoritativeGone,
    Archive,
    FirstObservation,
}

internal sealed record WatchDemandFormationReasonPresentation(
    string RawCode,
    string ChineseLabel,
    bool IsKnown);

internal sealed record WatchDemandFormationFactPresentation(
    WatchDemandFormationFactKind Kind,
    string Label,
    string Value,
    DateTimeOffset? OccurredAt,
    string? PollTraceId,
    string? ProjectionCommitId,
    long? SeriesSequence,
    bool IsAvailable = true,
    WatchInspectorText? Catalog = null)
{
    private WatchInspectorText Text => Catalog
        ?? WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Inspector;

    public string OccurrenceSummary => IsAvailable && OccurredAt is { } occurredAt
        ? Text.FormatOccurrence(WatchTimeDisplay.Format(occurredAt), SeriesSequence)
        : Text.MissingOccurrence;

    public string EvidenceSummary => IsAvailable
        ? Text.FormatEvidence(PollTraceId, ProjectionCommitId)
        : Text.MissingEvidence;

    public string AutomationName =>
        Text.FormatFactAutomation(Label, Value, OccurrenceSummary, EvidenceSummary);
}

internal enum WatchDemandMesBoundaryState
{
    NotApplicable,
    Missing,
    Unique,
    Conflict,
}

internal sealed record WatchDemandMesRawRowPresentation(
    int Ordinal,
    string PollTraceId,
    string ProjectionCommitId,
    MesObservationAssignment Assignment,
    string? SeriesId,
    string? DemandId,
    string? WorkType,
    string? Sublot,
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package,
    DateTimeOffset ObservedAt,
    string? MesSourceDateRaw);

internal sealed record WatchDemandMesBoundaryRawRowPresentation(
    string BoundaryLabel,
    WatchDemandMesRawRowPresentation RawRow);

internal sealed record WatchDemandMesObservationGroupPresentation(
    string PollTraceId,
    string ProjectionCommitId,
    MesObservationAssignment Assignment,
    IReadOnlyList<WatchDemandMesRawRowPresentation> Rows);

internal sealed record WatchDemandMesBoundarySidePresentation(
    string Label,
    string? DemandId,
    string? PollTraceId,
    string? ProjectionCommitId,
    WatchDemandMesBoundaryState State,
    IReadOnlyList<WatchDemandMesObservationGroupPresentation> ObservationGroups)
{
    public IReadOnlyList<WatchDemandMesRawRowPresentation> RawRowsInOrdinalOrder =>
        ObservationGroups
            .SelectMany(group => group.Rows)
            .OrderBy(row => row.Ordinal)
            .ToArray();
}

internal sealed record WatchDemandMesScalarFieldPresentation(
    string FieldName,
    string BeforeValue,
    string AfterValue,
    bool IsChanged,
    DateTimeOffset? BeforeMesSourceDate = null,
    DateTimeOffset? AfterMesSourceDate = null,
    WatchInspectorText? Catalog = null)
{
    private WatchInspectorText Text => Catalog
        ?? WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Inspector;

    public string ChangeLabel => IsChanged ? Text.Changed : Text.Unchanged;

    public string AutomationName =>
        Text.FormatScalarAutomation(FieldName, BeforeValue, AfterValue, ChangeLabel);
}

internal sealed record WatchDemandMesBoundaryPresentation(
    WatchDemandMesBoundarySidePresentation Before,
    WatchDemandMesBoundarySidePresentation After,
    bool CanProjectScalarFields,
    IReadOnlyList<WatchDemandMesScalarFieldPresentation> ScalarFields,
    string Explanation);

internal sealed record WatchDemandSeriesInspectorEventPresentation(
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
    string PayloadJson,
    IReadOnlyList<string> RelatedDemandIds);

internal sealed record WatchDemandSeriesFrozenSnapshotPresentation(
    string SnapshotReference,
    string ProjectionCommitId,
    long ProjectionSequence,
    DateTimeOffset ProjectionCommittedAt,
    string PollTraceId);

internal sealed record WatchDemandSeriesInspectorGenerationPresentation(
    int Generation,
    string DemandId,
    string? PredecessorDemandId,
    string Status,
    bool IsCurrent,
    WatchDemandFormationReasonPresentation FormationReason,
    IReadOnlyList<WatchDemandFormationFactPresentation> FormationFacts,
    WatchDemandMesBoundaryPresentation MesBoundary,
    WatchInspectorText? Catalog = null)
{
    private WatchInspectorText Text => Catalog
        ?? WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Inspector;

    public string CurrentMarker => IsCurrent ? Text.CurrentGeneration : Text.HistoricalGeneration;

    public string NavigationAutomationName =>
        Text.FormatGenerationNavigation(Generation, DemandId, Status, CurrentMarker);
}

internal sealed record WatchDemandSeriesInspectorPresentation(
    string SeriesId,
    string WorkType,
    string Sublot,
    string Lifecycle,
    string CurrentPresence,
    WatchDemandSeriesFrozenSnapshotPresentation FrozenSnapshot,
    IReadOnlyList<WatchDemandSeriesInspectorGenerationPresentation> Generations,
    WatchDemandSeriesInspectorGenerationPresentation FocusedGeneration,
    IReadOnlyList<WatchDemandSeriesInspectorEventPresentation> Events,
    WatchInspectorText? Catalog = null)
{
    private const string DemandCreatedEvent = "TRANSPORT_DEMAND_CREATED";

    private WatchInspectorText Text => Catalog
        ?? WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Inspector;

    public string EventsCountAutomationName => Text.FormatFrozenEventCount(Events.Count);

    public static WatchDemandSeriesInspectorPresentation Project(
        DemandSeriesDetailSnapshot snapshot,
        string? focusedDemandId,
        WatchInspectorText? text = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        text ??= WatchTextCatalog.For(WatchDisplayLanguage.SimplifiedChinese).Inspector;

        var series = snapshot.Series;
        var events = series.Events
            .OrderBy(seriesEvent => seriesEvent.SeriesSequence)
            .ToArray();
        var demands = series.Demands
            .OrderBy(demand => demand.Generation)
            .ToArray();
        var generations = demands
            .Select(demand => ProjectGeneration(series, demand, events, text))
            .ToArray();
        var focused = generations.FirstOrDefault(generation => string.Equals(
                generation.DemandId,
                focusedDemandId,
                StringComparison.Ordinal))
            ?? generations.First(generation => string.Equals(
                generation.DemandId,
                series.CurrentDemand.DemandId,
                StringComparison.Ordinal));

        return new WatchDemandSeriesInspectorPresentation(
            series.SeriesId,
            series.WorkType,
            series.Sublot,
            series.Lifecycle,
            series.CurrentPresence,
            new WatchDemandSeriesFrozenSnapshotPresentation(
                snapshot.SnapshotReference,
                snapshot.Snapshot.ProjectionCommitId,
                snapshot.Snapshot.ProjectionSequence,
                snapshot.Snapshot.ProjectionCommittedAt,
                snapshot.Snapshot.PollTraceId),
            generations,
            focused,
            events.Select(seriesEvent => ProjectEvent(seriesEvent, demands)).ToArray(),
            text);
    }

    public IReadOnlyList<WatchDemandSeriesInspectorEventPresentation> EventsForDemand(
        string? demandId) => string.IsNullOrWhiteSpace(demandId)
        ? Events
        : Events
            .Where(seriesEvent => seriesEvent.RelatedDemandIds.Contains(
                demandId,
                StringComparer.Ordinal))
            .ToArray();

    private static WatchDemandSeriesInspectorEventPresentation ProjectEvent(
        DemandSeriesEventSnapshot seriesEvent,
        IReadOnlyList<TransportDemandSnapshot> demands) => new(
        seriesEvent.EventId,
        seriesEvent.SeriesId,
        seriesEvent.SeriesSequence,
        seriesEvent.EventType,
        seriesEvent.OccurredAt,
        seriesEvent.SubjectKind,
        seriesEvent.SubjectId,
        seriesEvent.PollTraceId,
        seriesEvent.ProjectionCommitId,
        seriesEvent.PayloadVersion,
        seriesEvent.PayloadJson,
        ReadRelatedDemandIds(seriesEvent, demands));

    private static IReadOnlyList<string> ReadRelatedDemandIds(
        DemandSeriesEventSnapshot seriesEvent,
        IReadOnlyList<TransportDemandSnapshot> demands)
    {
        var knownDemandIds = demands
            .Select(demand => demand.DemandId)
            .ToHashSet(StringComparer.Ordinal);
        var related = new HashSet<string>(StringComparer.Ordinal);
        if (seriesEvent.SubjectId is { } subjectId && knownDemandIds.Contains(subjectId))
        {
            related.Add(subjectId);
        }

        try
        {
            using var document = JsonDocument.Parse(seriesEvent.PayloadJson);
            AddPayloadDemandId("demandId");
            AddPayloadDemandId("predecessorDemandId");

            void AddPayloadDemandId(string propertyName)
            {
                if (document.RootElement.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } demandId
                    && knownDemandIds.Contains(demandId))
                {
                    related.Add(demandId);
                }
            }
        }
        catch (JsonException)
        {
            // The immutable raw payload remains available; malformed filter metadata stays empty.
        }

        return related.Order(StringComparer.Ordinal).ToArray();
    }

    private static WatchDemandSeriesInspectorGenerationPresentation ProjectGeneration(
        DemandSeriesSnapshot series,
        TransportDemandSnapshot demand,
        IReadOnlyList<DemandSeriesEventSnapshot> events,
        WatchInspectorText text)
    {
        var creationEvents = events
            .Where(seriesEvent => IsDemandEvent(seriesEvent, DemandCreatedEvent, demand.DemandId))
            .ToArray();
        var creationEvent = creationEvents.Length == 1 ? creationEvents[0] : null;
        var reason = ProjectReason(demand, creationEvents, text);
        var facts = ProjectFormationFacts(series, demand, reason.RawCode, creationEvent, events, text);

        return new WatchDemandSeriesInspectorGenerationPresentation(
            demand.Generation,
            demand.DemandId,
            demand.PredecessorDemandId,
            demand.Status,
            string.Equals(demand.DemandId, series.CurrentDemand.DemandId, StringComparison.Ordinal),
            reason,
            facts,
            ProjectMesBoundary(series, demand, text),
            text);
    }

    private static WatchDemandMesBoundaryPresentation ProjectMesBoundary(
        DemandSeriesSnapshot series,
        TransportDemandSnapshot demand,
        WatchInspectorText text)
    {
        var predecessor = string.IsNullOrWhiteSpace(demand.PredecessorDemandId)
            ? null
            : series.Demands.FirstOrDefault(candidate => string.Equals(
                candidate.DemandId,
                demand.PredecessorDemandId,
                StringComparison.Ordinal));
        var beforeProjection = predecessor is null
            ? new BoundarySideProjection(
                new WatchDemandMesBoundarySidePresentation(
                    text.BeforeBoundary,
                    DemandId: null,
                    PollTraceId: null,
                    ProjectionCommitId: null,
                    WatchDemandMesBoundaryState.NotApplicable,
                    ObservationGroups: []),
                UniqueAssignedRow: null)
            : ProjectBoundarySide(
                text.BeforeBoundary,
                predecessor.DemandId,
                predecessor.LatestObservationPollTraceId,
                predecessor.LatestObservationProjectionCommitId,
                series.RawObservations);
        var afterProjection = ProjectBoundarySide(
            demand.Generation == 1 ? text.FirstBoundary : text.NewBoundary,
            demand.DemandId,
            demand.CreatedPollTraceId,
            demand.CreatedProjectionCommitId,
            series.RawObservations);
        var before = beforeProjection.Presentation;
        var after = afterProjection.Presentation;
        var beforeUnique = before.State is WatchDemandMesBoundaryState.NotApplicable
            or WatchDemandMesBoundaryState.Unique;
        var canProjectScalars = beforeUnique && after.State == WatchDemandMesBoundaryState.Unique;
        var fields = canProjectScalars
            ? ProjectScalarFields(
                beforeProjection.UniqueAssignedRow,
                afterProjection.UniqueAssignedRow!,
                text)
            : [];

        return new WatchDemandMesBoundaryPresentation(
            before,
            after,
            canProjectScalars,
            fields,
            text.MesExplanation);
    }

    private static BoundarySideProjection ProjectBoundarySide(
        string label,
        string demandId,
        string? pollTraceId,
        string? projectionCommitId,
        IReadOnlyList<DemandRawObservationSnapshot> observations)
    {
        if (string.IsNullOrWhiteSpace(pollTraceId)
            || string.IsNullOrWhiteSpace(projectionCommitId))
        {
            return new BoundarySideProjection(
                new WatchDemandMesBoundarySidePresentation(
                    label,
                    demandId,
                    pollTraceId,
                    projectionCommitId,
                    WatchDemandMesBoundaryState.Missing,
                    ObservationGroups: []),
                UniqueAssignedRow: null);
        }

        var matchingPoll = observations
            .Where(observation => string.Equals(
                    observation.PollTraceId,
                    pollTraceId,
                    StringComparison.Ordinal)
                && string.Equals(
                    observation.ProjectionCommitId,
                    projectionCommitId,
                    StringComparison.Ordinal))
            .OrderBy(observation => observation.Ordinal)
            .ToArray();
        var groups = matchingPoll
            .GroupBy(observation => observation.Assignment)
            .OrderBy(group => group.Key)
            .Select(group => new WatchDemandMesObservationGroupPresentation(
                pollTraceId,
                projectionCommitId,
                group.Key,
                group.Select(ProjectRawRow).ToArray()))
            .ToArray();
        var assignedRows = matchingPoll.Where(observation =>
                observation.Assignment == MesObservationAssignment.Assigned
                && string.Equals(observation.DemandId, demandId, StringComparison.Ordinal))
            .ToArray();
        var state = assignedRows.Length switch
        {
            0 => WatchDemandMesBoundaryState.Missing,
            1 => WatchDemandMesBoundaryState.Unique,
            _ => WatchDemandMesBoundaryState.Conflict,
        };

        return new BoundarySideProjection(
            new WatchDemandMesBoundarySidePresentation(
                label,
                demandId,
                pollTraceId,
                projectionCommitId,
                state,
                groups),
            assignedRows.Length == 1 ? assignedRows[0] : null);
    }

    private static WatchDemandMesRawRowPresentation ProjectRawRow(
        DemandRawObservationSnapshot observation) => new(
        observation.Ordinal,
        observation.PollTraceId,
        observation.ProjectionCommitId,
        observation.Assignment,
        observation.SeriesId,
        observation.DemandId,
        observation.WorkType,
        observation.Sublot,
        observation.Area,
        observation.Eqp,
        observation.Step,
        observation.MesSourceDate,
        observation.Package,
        observation.ObservedAt,
        observation.MesSourceDateRaw);

    private static IReadOnlyList<WatchDemandMesScalarFieldPresentation> ProjectScalarFields(
        DemandRawObservationSnapshot? before,
        DemandRawObservationSnapshot after,
        WatchInspectorText text)
    {
        var notApplicable = before is null;
        return
        [
            Field("TASK_TYPE", before?.WorkType, after.WorkType),
            Field("SUBLOT", before?.Sublot, after.Sublot),
            Field("AREA", before?.Area, after.Area),
            Field("EQP", before?.Eqp, after.Eqp),
            Field("STEP", before?.Step, after.Step),
            new WatchDemandMesScalarFieldPresentation(
                "DATES / MesSourceDate",
                notApplicable ? text.NotApplicable : Display(before!.MesSourceDate, text),
                Display(after.MesSourceDate, text),
                !notApplicable && before!.MesSourceDate != after.MesSourceDate,
                BeforeMesSourceDate: before?.MesSourceDate,
                AfterMesSourceDate: after.MesSourceDate,
                Catalog: text),
            Field("PACKAGE", before?.Package, after.Package),
        ];

        WatchDemandMesScalarFieldPresentation Field(
            string fieldName,
            string? beforeValue,
            string? afterValue) => new(
            fieldName,
                notApplicable ? text.NotApplicable : Display(beforeValue, text),
                Display(afterValue, text),
                !notApplicable && !string.Equals(beforeValue, afterValue, StringComparison.Ordinal),
                Catalog: text);
    }

    private static string Display(string? value, WatchInspectorText text) =>
        string.IsNullOrWhiteSpace(value) ? text.SourceNotProvided : value;

    private static string Display(DateTimeOffset? value, WatchInspectorText text) =>
        value is null ? text.SourceNotProvided : WatchTimeDisplay.Format(value.Value);

    private static WatchDemandFormationReasonPresentation ProjectReason(
        TransportDemandSnapshot demand,
        IReadOnlyList<DemandSeriesEventSnapshot> creationEvents,
        WatchInspectorText text)
    {
        if (creationEvents.Count != 1)
        {
            return UnknownReason(
                creationEvents.Count == 0 ? text.MissingCreation : text.ConflictingCreation,
                text);
        }

        var payloadReason = ReadReason(creationEvents[0].PayloadJson);
        if (payloadReason.State == ReasonPayloadState.Malformed)
        {
            return UnknownReason(text.InvalidReasonPayload, text);
        }

        var rawCode = payloadReason.Value;
        if (string.IsNullOrWhiteSpace(rawCode) && demand.Generation == 1)
        {
            rawCode = "FIRST_OBSERVED";
        }

        return rawCode switch
        {
            "FIRST_OBSERVED" => new(rawCode, text.DescribeReason(rawCode), IsKnown: true),
            "PREARCHIVE_REAPPEARANCE" => new(rawCode, text.DescribeReason(rawCode), IsKnown: true),
            "POSTARCHIVE_REAPPEARANCE" => new(rawCode, text.DescribeReason(rawCode), IsKnown: true),
            _ => UnknownReason(string.IsNullOrWhiteSpace(rawCode) ? text.MissingReasonCode : rawCode, text),
        };
    }

    private static IReadOnlyList<WatchDemandFormationFactPresentation> ProjectFormationFacts(
        DemandSeriesSnapshot series,
        TransportDemandSnapshot demand,
        string reasonCode,
        DemandSeriesEventSnapshot? creationEvent,
        IReadOnlyList<DemandSeriesEventSnapshot> events,
        WatchInspectorText text)
    {
        if (creationEvent is null)
        {
            return [];
        }

        if (string.Equals(reasonCode, "FIRST_OBSERVED", StringComparison.Ordinal))
        {
            return [EventFact(
                WatchDemandFormationFactKind.FirstObservation,
                text.FactLabel(WatchDemandFormationFactKind.FirstObservation),
                demand.DemandId,
                creationEvent,
                text)];
        }

        var isPrearchiveReappearance = string.Equals(
            reasonCode,
            "PREARCHIVE_REAPPEARANCE",
            StringComparison.Ordinal);
        var isPostarchiveReappearance = string.Equals(
            reasonCode,
            "POSTARCHIVE_REAPPEARANCE",
            StringComparison.Ordinal);
        if (!isPrearchiveReappearance && !isPostarchiveReappearance)
        {
            return [EventFact(
                WatchDemandFormationFactKind.FirstObservation,
                text.CreationEventFact,
                demand.DemandId,
                creationEvent,
                text)];
        }

        var facts = new List<WatchDemandFormationFactPresentation>();
        if (string.IsNullOrWhiteSpace(demand.PredecessorDemandId))
        {
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.PredecessorIdentity,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorIdentity),
                text));
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.PredecessorLastObservation,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorLastObservation),
                text));
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.AuthoritativeGone,
                text.FactLabel(WatchDemandFormationFactKind.AuthoritativeGone),
                text));
            if (isPostarchiveReappearance)
            {
                facts.Add(MissingFact(
                    WatchDemandFormationFactKind.Archive,
                    text.FactLabel(WatchDemandFormationFactKind.Archive),
                    text));
            }

            facts.Add(EventFact(
                WatchDemandFormationFactKind.FirstObservation,
                text.FactLabel(WatchDemandFormationFactKind.FirstObservation, isNewObservation: true),
                demand.DemandId,
                creationEvent,
                text));
            return facts;
        }

        var predecessor = series.Demands.FirstOrDefault(candidate => string.Equals(
            candidate.DemandId,
            demand.PredecessorDemandId,
            StringComparison.Ordinal));
        if (predecessor is null)
        {
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.PredecessorIdentity,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorIdentity),
                text));
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.PredecessorLastObservation,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorLastObservation),
                text));
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.AuthoritativeGone,
                text.FactLabel(WatchDemandFormationFactKind.AuthoritativeGone),
                text));
            if (isPostarchiveReappearance)
            {
                facts.Add(MissingFact(
                    WatchDemandFormationFactKind.Archive,
                    text.FactLabel(WatchDemandFormationFactKind.Archive),
                    text));
            }

            facts.Add(EventFact(
                WatchDemandFormationFactKind.FirstObservation,
                text.FactLabel(WatchDemandFormationFactKind.FirstObservation, isNewObservation: true),
                demand.DemandId,
                creationEvent,
                text));
            return facts;
        }

        var predecessorCreation = events.FirstOrDefault(seriesEvent =>
            IsDemandEvent(seriesEvent, DemandCreatedEvent, predecessor.DemandId));
        facts.Add(predecessorCreation is null
            ? SnapshotFact(
                WatchDemandFormationFactKind.PredecessorIdentity,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorIdentity),
                predecessor.DemandId,
                predecessor.CreatedAt,
                predecessor.CreatedPollTraceId,
                predecessor.CreatedProjectionCommitId,
                text)
            : EventFact(
                WatchDemandFormationFactKind.PredecessorIdentity,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorIdentity),
                predecessor.DemandId,
                predecessorCreation,
                text));

        if (!string.IsNullOrWhiteSpace(predecessor.LatestObservationPollTraceId)
            && !string.IsNullOrWhiteSpace(predecessor.LatestObservationProjectionCommitId)
            && predecessor.LatestObservationAt is { } latestObservationAt)
        {
            facts.Add(SnapshotFact(
                WatchDemandFormationFactKind.PredecessorLastObservation,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorLastObservation),
                predecessor.DemandId,
                latestObservationAt,
                predecessor.LatestObservationPollTraceId,
                predecessor.LatestObservationProjectionCommitId,
                text));
        }
        else
        {
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.PredecessorLastObservation,
                text.FactLabel(WatchDemandFormationFactKind.PredecessorLastObservation),
                text));
        }

        var goneEvent = events.LastOrDefault(seriesEvent =>
            IsDemandEvent(seriesEvent, "DEMAND_GONE", predecessor.DemandId)
            && seriesEvent.SeriesSequence < creationEvent.SeriesSequence);
        if (goneEvent is not null)
        {
            facts.Add(EventFact(
                WatchDemandFormationFactKind.AuthoritativeGone,
                text.FactLabel(WatchDemandFormationFactKind.AuthoritativeGone),
                predecessor.DemandId,
                goneEvent,
                text));
        }
        else
        {
            facts.Add(MissingFact(
                WatchDemandFormationFactKind.AuthoritativeGone,
                text.FactLabel(WatchDemandFormationFactKind.AuthoritativeGone),
                text));
        }

        if (isPostarchiveReappearance)
        {
            var archiveEvent = events.LastOrDefault(seriesEvent =>
                string.Equals(
                    seriesEvent.EventType,
                    "GONE_TIMEOUT_ARCHIVED",
                    StringComparison.Ordinal)
                && seriesEvent.SeriesSequence < creationEvent.SeriesSequence
                && PayloadReferencesDemand(seriesEvent.PayloadJson, predecessor.DemandId));
            if (archiveEvent is not null)
            {
                facts.Add(EventFact(
                    WatchDemandFormationFactKind.Archive,
                    text.FactLabel(WatchDemandFormationFactKind.Archive),
                    predecessor.DemandId,
                    archiveEvent,
                    text));
            }
            else
            {
                facts.Add(MissingFact(
                    WatchDemandFormationFactKind.Archive,
                    text.FactLabel(WatchDemandFormationFactKind.Archive),
                    text));
            }
        }

        facts.Add(EventFact(
            WatchDemandFormationFactKind.FirstObservation,
            text.FactLabel(WatchDemandFormationFactKind.FirstObservation, isNewObservation: true),
            demand.DemandId,
            creationEvent,
            text));
        return facts;
    }

    private static WatchDemandFormationFactPresentation MissingFact(
        WatchDemandFormationFactKind kind,
        string label,
        WatchInspectorText text) => new(
        kind,
        label,
        text.MissingFact,
        OccurredAt: null,
        PollTraceId: null,
        ProjectionCommitId: null,
        SeriesSequence: null,
        IsAvailable: false,
        Catalog: text);

    private static WatchDemandFormationFactPresentation EventFact(
        WatchDemandFormationFactKind kind,
        string label,
        string value,
        DemandSeriesEventSnapshot seriesEvent,
        WatchInspectorText text) => new(
        kind,
        label,
        value,
        seriesEvent.OccurredAt,
        seriesEvent.PollTraceId,
        seriesEvent.ProjectionCommitId,
        seriesEvent.SeriesSequence,
        IsAvailable: true,
        Catalog: text);

    private static WatchDemandFormationFactPresentation SnapshotFact(
        WatchDemandFormationFactKind kind,
        string label,
        string value,
        DateTimeOffset occurredAt,
        string pollTraceId,
        string projectionCommitId,
        WatchInspectorText text) => new(
        kind,
        label,
        value,
        occurredAt,
        pollTraceId,
        projectionCommitId,
        SeriesSequence: null,
        IsAvailable: true,
        Catalog: text);

    private static bool IsDemandEvent(
        DemandSeriesEventSnapshot seriesEvent,
        string eventType,
        string demandId) =>
        string.Equals(seriesEvent.EventType, eventType, StringComparison.Ordinal)
        && string.Equals(seriesEvent.SubjectKind, "DEMAND", StringComparison.OrdinalIgnoreCase)
        && string.Equals(seriesEvent.SubjectId, demandId, StringComparison.Ordinal);

    private static WatchDemandFormationReasonPresentation UnknownReason(
        string rawCode,
        WatchInspectorText text) =>
        new(rawCode, text.DescribeReason(rawCode), IsKnown: false);

    private static bool PayloadReferencesDemand(string payloadJson, string demandId)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("demandId", out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), demandId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (ReasonPayloadState State, string? Value) ReadReason(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("reason", out var reason))
            {
                return (ReasonPayloadState.Missing, null);
            }

            return reason.ValueKind == JsonValueKind.String
                ? (ReasonPayloadState.Present, reason.GetString())
                : (ReasonPayloadState.Malformed, null);
        }
        catch (JsonException)
        {
            return (ReasonPayloadState.Malformed, null);
        }
    }

    private enum ReasonPayloadState
    {
        Missing,
        Present,
        Malformed,
    }

    private sealed record BoundarySideProjection(
        WatchDemandMesBoundarySidePresentation Presentation,
        DemandRawObservationSnapshot? UniqueAssignedRow);
}
