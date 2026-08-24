using System.Text.Json;
using MesIngest.Core.SeriesProjection;
using MesIngest.LocalAdministration;

namespace MesIngest.Tests;

public sealed class LocalAdministrationCommandTests
{
    [Fact]
    public async Task Resume_command_uses_environment_secret_and_exact_operator_target()
    {
        var epoch = HistoryEpoch.FromGuid(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var administration = new RecordingAdministration(HealthyState(epoch));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await LocalAdministrationCommand.RunAsync(
            [
                "resume-storage-pressure",
                "--database", "MesIngest",
                "--history-epoch", epoch.Value.ToString("D"),
                "--reason", "volume expanded",
            ],
            output,
            error,
            _ => administration,
            name => name == LocalAdministrationCommand.DefaultConnectionStringEnvironment
                ? "Server=secret-not-on-command-line"
                : null);

        Assert.Equal(0, exitCode);
        Assert.Equal("MesIngest", administration.Request!.DatabaseName);
        Assert.Equal(epoch, administration.Request.HistoryEpoch);
        Assert.Equal("volume expanded", administration.Request.Reason);
        Assert.DoesNotContain("secret-not-on-command-line", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        using var body = JsonDocument.Parse(output.ToString());
        Assert.Equal("RECOVERED", body.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Rejected_recovery_returns_stable_code_and_never_reports_success()
    {
        var administration = new RecordingAdministration(
            new StoragePressureAdministrationException(
                StoragePressureAdministrationErrorCodes.Unauthorized,
                "not authorized"));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await LocalAdministrationCommand.RunAsync(
            [
                "resume-storage-pressure",
                "--database", "MesIngest",
                "--history-epoch", "22222222-2222-2222-2222-222222222222",
                "--reason", "volume expanded",
            ],
            output,
            error,
            _ => administration,
            _ => "connection");

        Assert.Equal(3, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        using var body = JsonDocument.Parse(error.ToString());
        Assert.Equal(
            StoragePressureAdministrationErrorCodes.Unauthorized,
            body.RootElement.GetProperty("code").GetString());
    }

    private static StoragePressureStateSnapshot HealthyState(HistoryEpoch epoch) => new(
        StoragePressureStatuses.Healthy,
        epoch,
        "MesIngest",
        @"D:\sql\MesIngest.mdf",
        VolumeSpaceSample.FromPercent(@"D:\", 1_000_000, 20m),
        new DateTimeOffset(2026, 8, 24, 11, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 8, 24, 10, 0, 0, TimeSpan.Zero),
        "pause-1",
        "low space",
        "audit-1");

    private sealed class RecordingAdministration : IStoragePressureAdministration
    {
        private readonly StoragePressureStateSnapshot? _result;
        private readonly Exception? _exception;

        public RecordingAdministration(StoragePressureStateSnapshot result) => _result = result;
        public RecordingAdministration(Exception exception) => _exception = exception;

        public StoragePressureRecoveryRequest? Request { get; private set; }

        public Task<StoragePressureStateSnapshot> ResumeStoragePressureAsync(
            StoragePressureRecoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return _exception is not null
                ? Task.FromException<StoragePressureStateSnapshot>(_exception)
                : Task.FromResult(_result!);
        }
    }
}
