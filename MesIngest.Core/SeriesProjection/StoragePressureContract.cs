namespace MesIngest.Core.SeriesProjection;

public enum StoragePressureDecision
{
    Healthy,
    CriticalWarning,
    EnterPause,
}

public sealed record VolumeSpaceSample(
    string VolumeRoot,
    long TotalBytes,
    long AvailableBytes)
{
    public decimal AvailablePercent =>
        TotalBytes <= 0 ? 0 : AvailableBytes * 100m / TotalBytes;

    public VolumeSpaceSample Validate()
    {
        if (string.IsNullOrWhiteSpace(VolumeRoot))
        {
            throw new ArgumentException("A resolved volume root is required.", nameof(VolumeRoot));
        }

        if (TotalBytes <= 0 || AvailableBytes < 0 || AvailableBytes > TotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AvailableBytes),
                "Volume byte counts must describe a positive, bounded capacity.");
        }

        return this;
    }

    public static VolumeSpaceSample FromPercent(
        string volumeRoot,
        long totalBytes,
        decimal availablePercent)
    {
        if (availablePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(availablePercent));
        }

        return new VolumeSpaceSample(
            volumeRoot,
            totalBytes,
            decimal.ToInt64(totalBytes * availablePercent / 100m)).Validate();
    }
}

public static class StoragePressurePolicy
{
    public const decimal WarningThresholdPercent = 15m;
    public const decimal PauseThresholdPercent = 10m;

    public static StoragePressureDecision Evaluate(VolumeSpaceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        sample.Validate();
        if (sample.AvailablePercent < PauseThresholdPercent)
        {
            return StoragePressureDecision.EnterPause;
        }

        return sample.AvailablePercent < WarningThresholdPercent
            ? StoragePressureDecision.CriticalWarning
            : StoragePressureDecision.Healthy;
    }

    public static bool IsSafeForRecovery(VolumeSpaceSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.Validate().AvailablePercent >= WarningThresholdPercent;
    }
}

public static class StoragePressureStatuses
{
    public const string Healthy = "HEALTHY";
    public const string Warning = "CRITICAL_WARNING";
    public const string Paused = "STORAGE_PRESSURE_PAUSE";

    public static IReadOnlyList<string> All { get; } = [Healthy, Warning, Paused];
}

public sealed record DatabaseFileLocation(string LogicalName, string PhysicalPath);

public sealed record ResolvedDatabaseVolume(
    string VolumeRoot,
    IReadOnlyList<string> DatabaseFilePaths);

public static class DatabaseVolumeResolver
{
    private static readonly char[] ForbiddenPathCharacters = ['*', '?', '%', '$'];

    public static ResolvedDatabaseVolume Resolve(
        IReadOnlyCollection<DatabaseFileLocation> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new InvalidDataException("The database reported no physical files.");
        }

        var paths = new List<string>(files.Count);
        string? requiredRoot = null;
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.LogicalName)
                || string.IsNullOrWhiteSpace(file.PhysicalPath)
                || file.PhysicalPath.IndexOfAny(ForbiddenPathCharacters) >= 0
                || !Path.IsPathFullyQualified(file.PhysicalPath))
            {
                throw new InvalidDataException(
                    "Every database file must have one exact, fully resolved physical path.");
            }

            var path = Path.GetFullPath(file.PhysicalPath);
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidDataException(
                    "A database file path did not resolve to a physical volume.");
            }

            requiredRoot ??= root;
            if (!string.Equals(requiredRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "All MesIngest database files must reside on the same monitored volume.");
            }

            paths.Add(path);
        }

        return new ResolvedDatabaseVolume(
            requiredRoot!,
            paths.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

public sealed record StoragePressureStateSnapshot(
    string Status,
    HistoryEpoch HistoryEpoch,
    string DatabaseName,
    string DatabaseFilePath,
    VolumeSpaceSample Space,
    DateTimeOffset ObservedAt,
    DateTimeOffset? PausedAt,
    string? PauseId,
    string? PauseReason,
    string? RecoveryAuditId)
{
    public bool IsPaused => string.Equals(
        Status,
        StoragePressureStatuses.Paused,
        StringComparison.Ordinal);
}

public sealed class IngestNotCurrentException : Exception
{
    public const string ErrorCode = "INGEST_NOT_CURRENT";

    public IngestNotCurrentException(string message) : base(message)
    {
    }
}

public interface IStoragePressurePollGate
{
    Task<bool> CanQueryMesAsync(CancellationToken cancellationToken = default);
}

public interface IVolumeSpaceReader
{
    VolumeSpaceSample Read(string resolvedVolumeRoot);
}

public sealed class PhysicalVolumeSpaceReader : IVolumeSpaceReader
{
    public VolumeSpaceSample Read(string resolvedVolumeRoot)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(resolvedVolumeRoot));
        if (string.IsNullOrWhiteSpace(root)
            || !string.Equals(root, resolvedVolumeRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Storage monitoring requires one exact volume root.");
        }

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
        {
            throw new IOException("The MesIngest database volume is not ready.");
        }

        return new VolumeSpaceSample(root, drive.TotalSize, drive.AvailableFreeSpace).Validate();
    }
}

public interface IStoragePressureOperations
{
    Task<ResolvedDatabaseVolume> ResolveDatabaseVolumeAsync(
        CancellationToken cancellationToken = default);

    Task<StoragePressureStateSnapshot> ObserveStoragePressureAsync(
        ResolvedDatabaseVolume databaseVolume,
        VolumeSpaceSample sample,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<StoragePressureStateSnapshot> ReadStoragePressureStateAsync(
        CancellationToken cancellationToken = default);
}

public sealed record StoragePressureRecoveryRequest(
    string DatabaseName,
    HistoryEpoch HistoryEpoch,
    string Reason);

public sealed record LocalAdministrationContext(
    string ExecutionIdentity,
    string DatabaseHost,
    bool IsLocalDatabaseHost,
    bool IsAuthorized);

public interface ILocalAdministrationContextProvider
{
    Task<LocalAdministrationContext> GetContextAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}

public interface IStoragePressureAdministration
{
    Task<StoragePressureStateSnapshot> ResumeStoragePressureAsync(
        StoragePressureRecoveryRequest request,
        CancellationToken cancellationToken = default);
}

public static class StoragePressureAdministrationErrorCodes
{
    public const string Unauthorized = "STORAGE_PRESSURE_RECOVERY_UNAUTHORIZED";
    public const string NotLocalDatabaseHost = "STORAGE_PRESSURE_RECOVERY_NOT_LOCAL";
    public const string WrongDatabase = "STORAGE_PRESSURE_RECOVERY_WRONG_DATABASE";
    public const string WrongHistoryEpoch = "STORAGE_PRESSURE_RECOVERY_WRONG_HISTORY_EPOCH";
    public const string NotPaused = "STORAGE_PRESSURE_RECOVERY_NOT_PAUSED";
    public const string UnsafeSpace = "STORAGE_PRESSURE_RECOVERY_UNSAFE_SPACE";
    public const string DatabaseUnhealthy = "STORAGE_PRESSURE_RECOVERY_DATABASE_UNHEALTHY";
}

public sealed class StoragePressureAdministrationException : Exception
{
    public StoragePressureAdministrationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public static class StoragePressureRecoveryPolicy
{
    public static void Validate(
        LocalAdministrationContext context,
        StoragePressureStateSnapshot state,
        StoragePressureRecoveryRequest request,
        VolumeSpaceSample currentSpace,
        bool databaseHealthy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentSpace);

        if (!context.IsAuthorized)
        {
            Throw(StoragePressureAdministrationErrorCodes.Unauthorized,
                "The execution identity is not authorized for local MesIngest administration.");
        }
        if (!context.IsLocalDatabaseHost)
        {
            Throw(StoragePressureAdministrationErrorCodes.NotLocalDatabaseHost,
                "StoragePressurePause recovery must run on the database host.");
        }
        if (!string.Equals(state.DatabaseName, request.DatabaseName, StringComparison.Ordinal))
        {
            Throw(StoragePressureAdministrationErrorCodes.WrongDatabase,
                "The requested database does not exactly match the configured database.");
        }
        if (state.HistoryEpoch != request.HistoryEpoch)
        {
            Throw(StoragePressureAdministrationErrorCodes.WrongHistoryEpoch,
                "The requested HistoryEpoch does not match the current database epoch.");
        }
        if (!state.IsPaused && state.RecoveryAuditId is null)
        {
            Throw(StoragePressureAdministrationErrorCodes.NotPaused,
                "The database is not in StoragePressurePause.");
        }
        if (!StoragePressurePolicy.IsSafeForRecovery(currentSpace))
        {
            Throw(StoragePressureAdministrationErrorCodes.UnsafeSpace,
                "The database volume has not recovered to the 15 percent safety threshold.");
        }
        if (!databaseHealthy)
        {
            Throw(StoragePressureAdministrationErrorCodes.DatabaseUnhealthy,
                "The target database is not ONLINE and READ_WRITE.");
        }
    }

    private static void Throw(string code, string message) =>
        throw new StoragePressureAdministrationException(code, message);
}
