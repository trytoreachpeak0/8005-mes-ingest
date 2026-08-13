using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class DemandSeriesArchivePolicyTests
{
    public static TheoryData<DateTimeOffset, DateTimeOffset, bool> BoundaryCases => new()
    {
        {
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 11, 59, 59, TimeSpan.Zero).AddTicks(9_999_999),
            false
        },
        {
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            true
        },
        {
            new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero).AddTicks(1),
            true
        },
        {
            new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 8, 13, 14, 0, 0, TimeSpan.FromHours(2)),
            true
        },
        {
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 13, 11, 0, 0, TimeSpan.Zero),
            false
        },
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void Archive_due_uses_twelve_hours_of_host_utc_from_gone_confirmation(
        DateTimeOffset goneConfirmedAt,
        DateTimeOffset completedAt,
        bool expected)
    {
        Assert.Equal(TimeSpan.FromHours(12), DemandSeriesArchivePolicy.MinimumGoneDuration);
        Assert.Equal(
            expected,
            DemandSeriesArchivePolicy.IsDue(
                goneConfirmedAt,
                completedAt,
                absenceAuthority: true));
    }

    [Fact]
    public void Archive_due_requires_authoritative_success()
    {
        var goneConfirmedAt = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
        var completedAt = goneConfirmedAt.AddHours(24);

        Assert.False(DemandSeriesArchivePolicy.IsDue(
            goneConfirmedAt,
            completedAt,
            absenceAuthority: false));
        Assert.True(DemandSeriesArchivePolicy.IsDue(
            goneConfirmedAt,
            completedAt,
            absenceAuthority: true));
    }
}
