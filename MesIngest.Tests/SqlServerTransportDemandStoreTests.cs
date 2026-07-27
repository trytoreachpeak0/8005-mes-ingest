using MesIngest.Core;
using Microsoft.Data.SqlClient;

namespace MesIngest.Tests;

[CollectionDefinition("SqlServer", DisableParallelization = true)]
public sealed class SqlServerCollectionDefinition;

[Collection("SqlServer")]
public class SqlServerTransportDemandStoreTests
{
    [SqlServerAvailabilityFact]
    public void Persisted_demands_and_pauses_are_readable_after_new_store_instance()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var state = new ProjectionState(
            [
                new TransportDemand
                {
                    DemandId = "d-visible",
                    TaskType = "DIE_TO_WIRE_STAGING",
                    Sublot = "Q-VIS",
                    Area = "N09-01",
                    Eqp = "EQ1",
                    Step = "焊线",
                    Dates = now,
                    Package = "PKG-V",
                    Status = DemandStatus.Visible,
                    MesLastSeenAt = now,
                    DisappearCount = 0,
                    LocationRisk = false,
                    CreatedAt = now.AddHours(-2),
                },
                new TransportDemand
                {
                    DemandId = "d-gone",
                    TaskType = "DIE_TO_OVEN",
                    Sublot = "Q-GONE",
                    Area = null,
                    Eqp = "EQ2",
                    Step = "烘箱",
                    Dates = now.AddHours(-1),
                    Package = "PKG-G",
                    Status = DemandStatus.Gone,
                    MesLastSeenAt = now.AddMinutes(-20),
                    DisappearCount = 2,
                    LocationRisk = true,
                    LocationRiskCode = "AREA_EMPTY",
                    CreatedAt = now.AddHours(-3),
                    GoneAt = now.AddMinutes(-15),
                },
            ],
            [
                new TaskTypePauseState(
                    "DIE_TO_OVEN",
                    PausedZeroDrop: true,
                    LastHealthyNonZeroCount: 12,
                    RecoveryStreak: 0),
            ]);

        var writer = new SqlServerTransportDemandStore(cs);
        writer.ReplaceState(state);

        var reader = new SqlServerTransportDemandStore(cs);
        var reloaded = reader.GetState();

        Assert.Equal(2, reloaded.Demands.Count);
        var visible = Assert.Single(reloaded.Demands, d => d.DemandId == "d-visible");
        Assert.Equal(DemandStatus.Visible, visible.Status);
        Assert.Equal("PKG-V", visible.Package);
        Assert.Equal(0, visible.DisappearCount);
        Assert.False(visible.LocationRisk);
        Assert.Equal(now.AddHours(-2), visible.CreatedAt);
        Assert.Null(visible.GoneAt);

        var gone = Assert.Single(reloaded.Demands, d => d.DemandId == "d-gone");
        Assert.Equal(DemandStatus.Gone, gone.Status);
        Assert.Equal(2, gone.DisappearCount);
        Assert.True(gone.LocationRisk);
        Assert.Equal("AREA_EMPTY", gone.LocationRiskCode);
        Assert.Null(gone.Area);
        Assert.Equal(now.AddHours(-3), gone.CreatedAt);
        Assert.Equal(now.AddMinutes(-15), gone.GoneAt);

        var pause = Assert.Single(reloaded.TaskTypePauses);
        Assert.Equal("DIE_TO_OVEN", pause.TaskType);
        Assert.True(pause.PausedZeroDrop);
        Assert.Equal(12, pause.LastHealthyNonZeroCount);
        Assert.Equal(0, pause.RecoveryStreak);

        Assert.Equal("d-visible", reader.GetById("d-visible")?.DemandId);
        Assert.Single(reader.List(DemandStatus.Visible));
        Assert.Single(reader.List(DemandStatus.Gone));
    }

    [SqlServerAvailabilityFact]
    public void Persisted_alerts_and_poll_health_are_readable_after_new_store_instance()
    {
        var cs = SqlServerTestEnv.ConnectionString!;
        SqlServerTestEnv.WipeProjection(cs);

        var started = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.FromHours(8));
        var ended = started.AddSeconds(3);

        var writer = new SqlServerTransportDemandStore(cs);
        writer.AppendAlerts(
        [
            new IngestAlert(
                Code: "FIELD_DRIFT",
                TaskType: "DIE_TO_WIRE_STAGING",
                Sublot: "Q1",
                DemandId: "d1",
                Message: "drift"),
            new IngestAlert(
                Code: "PAUSED_ZERO_DROP",
                TaskType: "DIE_TO_OVEN",
                Message: "paused"),
        ]);
        writer.SetLatestPollHealth(new PollHealth(
            StartedAt: started,
            EndedAt: ended,
            DurationMs: 3000,
            RowCount: 42,
            Success: true,
            Outcome: "SUCCESS"));

        var reader = new SqlServerTransportDemandStore(cs);
        var alerts = reader.ListAlerts();
        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Code == "FIELD_DRIFT" && a.DemandId == "d1");
        Assert.Contains(alerts, a => a.Code == "PAUSED_ZERO_DROP" && a.TaskType == "DIE_TO_OVEN");
        Assert.All(alerts, a => Assert.NotNull(a.CreatedAt));
        Assert.Equal("PAUSED_ZERO_DROP", alerts[0].Code);

        var health = reader.GetLatestPollHealth();
        Assert.NotNull(health);
        Assert.Equal(started, health.StartedAt);
        Assert.Equal(ended, health.EndedAt);
        Assert.Equal(3000, health.DurationMs);
        Assert.Equal(42, health.RowCount);
        Assert.True(health.Success);
        Assert.Equal("SUCCESS", health.Outcome);
    }
}

internal static class SqlServerTestEnv
{
    public static string? ConnectionString { get; } = Resolve();

    public static bool IsAvailable => ConnectionString is not null;

    private static string? Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MES_INGEST_SQLSERVER");
        var candidates = new[]
        {
            fromEnv,
            @"Server=(localdb)\MSSQLLocalDB;Database=MesIngest_Ticket05_Smoke;Trusted_Connection=True;TrustServerCertificate=True",
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var builder = new SqlConnectionStringBuilder(candidate);
                var database = builder.InitialCatalog;
                builder.InitialCatalog = "master";
                using var conn = new SqlConnection(builder.ConnectionString);
                conn.Open();
                if (!string.IsNullOrWhiteSpace(database))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"""
                        IF DB_ID(N'{database.Replace("'", "''")}') IS NULL
                            CREATE DATABASE [{database.Replace("]", "]]")}];
                        """;
                    cmd.ExecuteNonQuery();
                }

                return candidate;
            }
            catch (SqlException)
            {
                // try next candidate
            }
            catch (InvalidOperationException)
            {
                // try next candidate
            }
        }

        return null;
    }

    public static void WipeProjection(string connectionString)
    {
        var store = new SqlServerTransportDemandStore(connectionString);
        store.ReplaceState(ProjectionState.Empty);

        using var conn = new SqlConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM dbo.IngestAlerts;
            DELETE FROM dbo.PollHealth;
            """;
        cmd.ExecuteNonQuery();
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SqlServerAvailabilityFactAttribute : FactAttribute
{
    public SqlServerAvailabilityFactAttribute()
    {
        if (!SqlServerTestEnv.IsAvailable)
        {
            Skip = "SQL Server unavailable (set MES_INGEST_SQLSERVER or enable LocalDB)";
        }
    }
}
