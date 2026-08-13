namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Decides whether an already-GONE DemandSeries may be archived by the current
/// complete SUCCESS round. The elapsed duration is based only on Host evidence.
/// </summary>
public static class DemandSeriesArchivePolicy
{
    public static readonly TimeSpan MinimumGoneDuration = TimeSpan.FromHours(12);

    public static bool IsDue(
        DateTimeOffset goneConfirmedAt,
        DateTimeOffset completedAt,
        bool absenceAuthority)
    {
        if (!absenceAuthority)
        {
            return false;
        }

        var goneUtc = goneConfirmedAt.ToUniversalTime();
        var completedUtc = completedAt.ToUniversalTime();
        return completedUtc >= goneUtc
            && completedUtc - goneUtc >= MinimumGoneDuration;
    }
}
