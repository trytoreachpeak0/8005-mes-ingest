namespace MesIngest.Core.SeriesProjection;

public static class LocalAdministrationOperations
{
    public const string StoragePressureRecovery = "STORAGE_PRESSURE_RECOVERY";
    public const string HistoryResetAcknowledgement = "HISTORY_RESET_ACKNOWLEDGEMENT";
}

public interface IMesIngestLocalAdministration
{
    Task<StoragePressureStateSnapshot> ResumeStoragePressureAsync(
        StoragePressureRecoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<HistoryResetStateSnapshot> AcknowledgeHistoryResetAsync(
        HistoryResetAcknowledgementRequest request,
        CancellationToken cancellationToken = default);
}
