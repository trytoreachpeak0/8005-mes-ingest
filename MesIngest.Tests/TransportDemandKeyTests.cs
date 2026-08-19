using MesIngest.Core.SeriesProjection;

namespace MesIngest.Tests;

/// <summary>
/// The contract fixes one TransportDemandKey comparison rule
/// (<see cref="NewMesIngestContract.KeyComparison"/>) and one implementation of it.
/// These assert that rule through the token every layer keys on.
/// </summary>
public sealed class TransportDemandKeyTests
{
    [Fact]
    public void Equal_components_produce_the_same_token()
    {
        Assert.Equal(
            TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "Q-1"),
            TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "Q-1"));
    }

    [Fact]
    public void Work_type_and_sublot_both_participate_in_identity()
    {
        var token = TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "Q-1");

        Assert.NotEqual(token, TransportDemandKeyIdentity.CreateToken("WIRE_TO_NITROGEN", "Q-1"));
        Assert.NotEqual(token, TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "Q-2"));
    }

    [Fact]
    public void Identity_is_ordinal_and_case_sensitive()
    {
        var token = TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "Q-1");

        Assert.NotEqual(token, TransportDemandKeyIdentity.CreateToken("wire_to_gate", "Q-1"));
        Assert.NotEqual(token, TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "q-1"));
    }

    [Fact]
    public void Component_boundaries_cannot_be_shifted_between_work_type_and_sublot()
    {
        Assert.NotEqual(
            TransportDemandKeyIdentity.CreateToken("WIRE_TO", "GATE-Q-1"),
            TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "-Q-1"));
    }

    [Theory]
    [InlineData(null, "Q-1")]
    [InlineData("", "Q-1")]
    [InlineData("   ", "Q-1")]
    [InlineData("WIRE_TO_GATE", null)]
    [InlineData("WIRE_TO_GATE", "")]
    [InlineData("WIRE_TO_GATE", "   ")]
    public void Null_or_blank_components_are_rejected(string? workType, string? sublot)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => TransportDemandKeyIdentity.CreateToken(workType!, sublot!));
    }

    [Fact]
    public void Surrounding_whitespace_is_preserved_rather_than_normalized_away()
    {
        Assert.NotEqual(
            TransportDemandKeyIdentity.CreateToken(" WIRE_TO_GATE ", " Q-1 "),
            TransportDemandKeyIdentity.CreateToken("WIRE_TO_GATE", "Q-1"));
    }
}
