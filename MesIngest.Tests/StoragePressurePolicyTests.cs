using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class StoragePressurePolicyTests
{
    [Theory]
    [InlineData(15, StoragePressureDecision.Healthy)]
    [InlineData(14.999, StoragePressureDecision.CriticalWarning)]
    [InlineData(10, StoragePressureDecision.CriticalWarning)]
    [InlineData(9.999, StoragePressureDecision.EnterPause)]
    public void Exact_free_space_boundaries_choose_the_documented_action(
        decimal availablePercent,
        StoragePressureDecision expected)
    {
        var sample = VolumeSpaceSample.FromPercent(
            @"D:\",
            totalBytes: 1_000_000,
            availablePercent);

        Assert.Equal(expected, StoragePressurePolicy.Evaluate(sample));
    }

    [Fact]
    public void Recovery_requires_the_warning_threshold_not_merely_the_pause_threshold()
    {
        Assert.False(StoragePressurePolicy.IsSafeForRecovery(
            VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 14.999m)));
        Assert.True(StoragePressurePolicy.IsSafeForRecovery(
            VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 15m)));
    }

    [Theory]
    [InlineData(@"%TEMP%\MesIngest.mdf")]
    [InlineData(@"D:\data\*.mdf")]
    [InlineData(@"D:\data\db?.ndf")]
    [InlineData(@"relative\MesIngest.mdf")]
    public void Database_volume_resolution_rejects_unresolved_or_non_exact_paths(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            DatabaseVolumeResolver.Resolve(
                [new DatabaseFileLocation("MesIngest", path)]));
    }

    [Fact]
    public void Database_volume_resolution_uses_every_actual_file_and_rejects_wrong_disk_substitution()
    {
        var resolved = DatabaseVolumeResolver.Resolve(
        [
            new DatabaseFileLocation("MesIngest", @"D:\sql\MesIngest.mdf"),
            new DatabaseFileLocation("MesIngest_log", @"D:\sql\MesIngest_log.ldf"),
        ]);

        Assert.Equal(@"D:\", resolved.VolumeRoot);
        Assert.Equal(2, resolved.DatabaseFilePaths.Count);

        Assert.Throws<InvalidDataException>(() => DatabaseVolumeResolver.Resolve(
        [
            new DatabaseFileLocation("MesIngest", @"D:\sql\MesIngest.mdf"),
            new DatabaseFileLocation("MesIngest_log", @"E:\sql\MesIngest_log.ldf"),
        ]));
    }

    [Theory]
    [InlineData(false, true, true, true, true, true, StoragePressureAdministrationErrorCodes.Unauthorized)]
    [InlineData(true, false, true, true, true, true, StoragePressureAdministrationErrorCodes.NotLocalDatabaseHost)]
    [InlineData(true, true, false, true, true, true, StoragePressureAdministrationErrorCodes.WrongDatabase)]
    [InlineData(true, true, true, false, true, true, StoragePressureAdministrationErrorCodes.WrongHistoryEpoch)]
    [InlineData(true, true, true, true, false, true, StoragePressureAdministrationErrorCodes.UnsafeSpace)]
    [InlineData(true, true, true, true, true, false, StoragePressureAdministrationErrorCodes.DatabaseUnhealthy)]
    public void Recovery_policy_rejects_each_failed_precondition_without_opening_the_gate(
        bool authorized,
        bool local,
        bool exactDatabase,
        bool exactEpoch,
        bool safeSpace,
        bool databaseHealthy,
        string expectedCode)
    {
        var epoch = HistoryEpoch.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var state = PausedState(epoch, "MesIngest");
        var request = new StoragePressureRecoveryRequest(
            exactDatabase ? "MesIngest" : "WrongDatabase",
            exactEpoch ? epoch : HistoryEpoch.CreateNew(),
            "operator verified storage");
        var context = new LocalAdministrationContext(
            "operator",
            "DBHOST",
            local,
            authorized);
        var sample = VolumeSpaceSample.FromPercent(
            @"D:\",
            1_000_000,
            safeSpace ? 15m : 14.999m);

        var error = Assert.Throws<StoragePressureAdministrationException>(() =>
            StoragePressureRecoveryPolicy.Validate(
                context,
                state,
                request,
                sample,
                databaseHealthy));

        Assert.Equal(expectedCode, error.Code);
        Assert.True(state.IsPaused);
    }

    private static StoragePressureStateSnapshot PausedState(
        HistoryEpoch epoch,
        string databaseName) => new(
            StoragePressureStatuses.Paused,
            epoch,
            databaseName,
            @"D:\sql\MesIngest.mdf",
            VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 9m),
            new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero),
            "pause-1",
            "low space",
            null);
}
