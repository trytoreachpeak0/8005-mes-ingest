using MesIngest.Core.SeriesProjection;

namespace MesIngest.Watch;

/// <summary>
/// Window-owned state for the explicitly requested raw-evidence escape hatch.
/// It is intentionally separate from the Error Search session snapshot: a raw
/// denial or limit failure must never erase the bounded list/detail evidence.
/// </summary>
internal sealed record WatchErrorRawEvidenceState(
    string? SnapshotReference,
    string? SeriesId,
    string? EvidenceId,
    bool IsLoading,
    ErrorSearchRawEvidenceSnapshot? Snapshot,
    DateTimeOffset? LastFailureAt,
    string? FailureCode,
    string? ErrorMessage,
    string? CorrelationId)
{
    public static WatchErrorRawEvidenceState Empty { get; } = new(
        SnapshotReference: null,
        SeriesId: null,
        EvidenceId: null,
        IsLoading: false,
        Snapshot: null,
        LastFailureAt: null,
        FailureCode: null,
        ErrorMessage: null,
        CorrelationId: null);

    public static WatchErrorRawEvidenceState Begin(
        string snapshotReference,
        string seriesId,
        string evidenceId,
        WatchErrorRawEvidenceState? retained = null)
    {
        ValidateIdentity(snapshotReference, seriesId, evidenceId);
        return new WatchErrorRawEvidenceState(
            snapshotReference,
            seriesId,
            evidenceId,
            IsLoading: true,
            RetainedSnapshot(retained, snapshotReference, seriesId, evidenceId),
            LastFailureAt: null,
            FailureCode: null,
            ErrorMessage: null,
            CorrelationId: null);
    }

    public static WatchErrorRawEvidenceState Loaded(
        ErrorSearchRawEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot.SnapshotReference, snapshot.SeriesId, snapshot.EvidenceId);
        return new WatchErrorRawEvidenceState(
            snapshot.SnapshotReference,
            snapshot.SeriesId,
            snapshot.EvidenceId,
            IsLoading: false,
            snapshot,
            LastFailureAt: null,
            FailureCode: null,
            ErrorMessage: null,
            CorrelationId: null);
    }

    public static WatchErrorRawEvidenceState Failed(
        string snapshotReference,
        string seriesId,
        string evidenceId,
        DateTimeOffset failedAt,
        string? failureCode,
        string? errorMessage,
        string? correlationId = null,
        WatchErrorRawEvidenceState? retained = null)
    {
        ValidateIdentity(snapshotReference, seriesId, evidenceId);
        return new WatchErrorRawEvidenceState(
            snapshotReference,
            seriesId,
            evidenceId,
            IsLoading: false,
            RetainedSnapshot(retained, snapshotReference, seriesId, evidenceId),
            failedAt,
            failureCode,
            errorMessage,
            correlationId);
    }

    private static ErrorSearchRawEvidenceSnapshot? RetainedSnapshot(
        WatchErrorRawEvidenceState? retained,
        string snapshotReference,
        string seriesId,
        string evidenceId) => retained?.Snapshot is { } snapshot
        && string.Equals(snapshotReference, retained.SnapshotReference, StringComparison.Ordinal)
        && string.Equals(seriesId, retained.SeriesId, StringComparison.Ordinal)
        && string.Equals(evidenceId, retained.EvidenceId, StringComparison.Ordinal)
            ? snapshot
            : null;

    private static void ValidateIdentity(
        string snapshotReference,
        string seriesId,
        string evidenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
    }
}
