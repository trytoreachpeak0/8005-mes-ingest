using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

public sealed class MesTaskUnionRoundDigestTests
{
    [Fact]
    public void Canonical_digest_treats_raw_rows_as_an_order_insensitive_multiset()
    {
        var sourceDate = new DateTimeOffset(2026, 8, 12, 8, 30, 0, TimeSpan.FromHours(8));
        var first = new MesTaskUnionObservation(
            WorkType: "WIRE_TO_NITROGEN",
            Sublot: "SL-DIGEST-A",
            Area: null,
            Eqp: "",
            Step: "STEP-A",
            MesSourceDate: sourceDate,
            Package: "PKG-A");
        var second = new MesTaskUnionObservation(
            WorkType: null,
            Sublot: "SL-DIGEST-B",
            Area: "A1-1",
            Eqp: "EQP-B",
            Step: null,
            MesSourceDate: null,
            Package: "");

        var original = MesTaskUnionRoundDigest.Compute([first, second, first]);
        var reorderedWithEquivalentOffset = MesTaskUnionRoundDigest.Compute(
        [
            first with { MesSourceDate = sourceDate.ToUniversalTime() },
            first,
            second,
        ]);
        var oneDuplicateRemoved = MesTaskUnionRoundDigest.Compute([first, second]);

        Assert.Matches("^[0-9a-f]{64}$", original);
        Assert.Equal(original, reorderedWithEquivalentOffset);
        Assert.NotEqual(original, oneDuplicateRemoved);
    }
}
