namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// The complete externally readable Demand catalog. This contract deliberately
/// has no Dispatch scope (AREA, WorkType, vehicle, map, or station filters).
/// </summary>
public sealed record ExternallyReadableDemandCatalogSnapshot(
    long CatalogRevision,
    string? ProjectionCommitId,
    long? ProjectionSequence,
    DateTimeOffset? ProjectionCommittedAt,
    IReadOnlyList<ExternallyReadableDemandSnapshot> Items)
{
    public ExternallyReadableDemandCatalogSnapshot Validate()
    {
        if (CatalogRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CatalogRevision),
                "A catalog revision cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(Items);
        if (CatalogRevision == 0 && Items.Count == 0)
        {
            if (ProjectionCommitId is not null
                || ProjectionSequence is not null
                || ProjectionCommittedAt is not null)
            {
                throw new InvalidDataException(
                    "The initial empty catalog must not claim a projection commit.");
            }

            return this;
        }

        if (string.IsNullOrWhiteSpace(ProjectionCommitId)
            || ProjectionSequence is null
            || ProjectionCommittedAt is null)
        {
            throw new InvalidDataException(
                "A changed catalog must identify the projection commit that produced it.");
        }

        return this;
    }
}

public sealed record ExternallyReadableDemandSnapshot(
    string DemandId,
    string SeriesId,
    string WorkType,
    string Sublot,
    int Generation,
    long DemandRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset ValueObservedAt,
    string ValuePollTraceId,
    string ValueProjectionCommitId,
    LiveMesFieldSetSnapshot LiveMesFields);

public sealed record ExternallyReadableDemandCatalogRead(
    long CatalogRevision,
    bool NotModified,
    ExternallyReadableDemandCatalogSnapshot? Snapshot)
{
    public static ExternallyReadableDemandCatalogRead Complete(
        ExternallyReadableDemandCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        return new(snapshot.CatalogRevision, NotModified: false, snapshot);
    }

    public static ExternallyReadableDemandCatalogRead Unchanged(long catalogRevision)
    {
        if (catalogRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(catalogRevision));
        }

        return new(catalogRevision, NotModified: true, Snapshot: null);
    }
}

/// <summary>
/// Central, auditable qualification policy. Clients must consume the resulting
/// catalog and must not duplicate these rules.
/// </summary>
public static class ExternallyReadableDemandPolicy
{
    public static bool IsEligible(
        string seriesLifecycle,
        string demandStatus,
        int currentRawObservationCount,
        LiveMesFieldSetSnapshot? liveMesFields,
        IReadOnlyCollection<string> currentConditionCodes)
    {
        ArgumentNullException.ThrowIfNull(currentConditionCodes);
        return string.Equals(
                seriesLifecycle,
                DemandSeriesLifecycleContract.Tracking,
                StringComparison.Ordinal)
            && string.Equals(
                demandStatus,
                DemandSeriesLifecycleContract.Visible,
                StringComparison.Ordinal)
            && currentRawObservationCount == 1
            && liveMesFields is not null
            && currentConditionCodes.Count == 0
            && MesFieldValidation.Evaluate(liveMesFields).Issues.Count == 0;
    }
}
