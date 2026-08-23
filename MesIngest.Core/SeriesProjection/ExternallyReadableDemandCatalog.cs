namespace MesIngest.Core.SeriesProjection;

public sealed record ExternallyReadableDemandCatalogIdentity(
    HistoryEpoch HistoryEpoch,
    long CatalogRevision)
{
    public ExternallyReadableDemandCatalogIdentity Validate()
    {
        ArgumentNullException.ThrowIfNull(HistoryEpoch);
        if (CatalogRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CatalogRevision),
                "A catalog revision cannot be negative.");
        }

        return this;
    }
}

public static class ExternallyReadableDemandCatalogEtagCodec
{
    private const string EpochPrefix = "catalog-h";
    private const string RevisionSeparator = "-r";

    public static string FormatOpaqueTag(ExternallyReadableDemandCatalogIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        identity.Validate();
        return $"{EpochPrefix}{identity.HistoryEpoch.Value:N}{RevisionSeparator}{identity.CatalogRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseOpaqueTag(
        ReadOnlySpan<char> opaqueTag,
        out ExternallyReadableDemandCatalogIdentity? identity)
    {
        identity = null;
        var revisionSeparatorIndex = opaqueTag.IndexOf(
            RevisionSeparator,
            StringComparison.Ordinal);
        if (!opaqueTag.StartsWith(EpochPrefix, StringComparison.Ordinal)
            || revisionSeparatorIndex < 0
            || !Guid.TryParseExact(
                opaqueTag[EpochPrefix.Length..revisionSeparatorIndex],
                "N",
                out var historyEpoch)
            || historyEpoch == Guid.Empty
            || !long.TryParse(
                opaqueTag[(revisionSeparatorIndex + RevisionSeparator.Length)..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var revision)
            || revision < 0)
        {
            return false;
        }

        identity = new ExternallyReadableDemandCatalogIdentity(
            HistoryEpoch.FromGuid(historyEpoch),
            revision);
        return true;
    }
}

/// <summary>
/// The complete externally readable Demand catalog. This contract deliberately
/// has no Dispatch scope (AREA, WorkType, vehicle, map, or station filters).
/// </summary>
public sealed record ExternallyReadableDemandCatalogSnapshot(
    HistoryEpoch HistoryEpoch,
    long CatalogRevision,
    string? ProjectionCommitId,
    long? ProjectionSequence,
    DateTimeOffset? ProjectionCommittedAt,
    IReadOnlyList<ExternallyReadableDemandSnapshot> Items)
{
    public ExternallyReadableDemandCatalogSnapshot Validate()
    {
        ArgumentNullException.ThrowIfNull(HistoryEpoch);
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
    ExternallyReadableDemandCatalogIdentity Identity,
    bool NotModified,
    ExternallyReadableDemandCatalogSnapshot? Snapshot)
{
    public HistoryEpoch HistoryEpoch => Identity.HistoryEpoch;

    public long CatalogRevision => Identity.CatalogRevision;

    public static ExternallyReadableDemandCatalogRead Complete(
        ExternallyReadableDemandCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        return new(
            new ExternallyReadableDemandCatalogIdentity(
                snapshot.HistoryEpoch,
                snapshot.CatalogRevision),
            NotModified: false,
            snapshot);
    }

    public static ExternallyReadableDemandCatalogRead Unchanged(
        ExternallyReadableDemandCatalogIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(identity.Validate(), NotModified: true, Snapshot: null);
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
