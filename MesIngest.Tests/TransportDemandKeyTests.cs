using MesIngest.Core;

namespace MesIngest.Tests;

public sealed class TransportDemandKeyTests
{
    [Fact]
    public void Equal_components_have_value_equality()
    {
        var first = new TransportDemandKey("WIRE_TO_GATE", "Q-1");
        var second = new TransportDemandKey("WIRE_TO_GATE", "Q-1");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Task_type_and_sublot_both_participate_in_identity()
    {
        var key = new TransportDemandKey("WIRE_TO_GATE", "Q-1");

        Assert.NotEqual(key, new TransportDemandKey("WIRE_TO_NITROGEN", "Q-1"));
        Assert.NotEqual(key, new TransportDemandKey("WIRE_TO_GATE", "Q-2"));
    }

    [Fact]
    public void Equality_is_ordinal_and_case_sensitive()
    {
        var key = new TransportDemandKey("WIRE_TO_GATE", "Q-1");

        Assert.NotEqual(key, new TransportDemandKey("wire_to_gate", "Q-1"));
        Assert.NotEqual(key, new TransportDemandKey("WIRE_TO_GATE", "q-1"));
    }

    [Theory]
    [InlineData(null, "Q-1")]
    [InlineData("", "Q-1")]
    [InlineData("   ", "Q-1")]
    [InlineData("WIRE_TO_GATE", null)]
    [InlineData("WIRE_TO_GATE", "")]
    [InlineData("WIRE_TO_GATE", "   ")]
    public void Null_or_blank_components_are_rejected(string? taskType, string? sublot)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new TransportDemandKey(taskType!, sublot!));
    }

    [Fact]
    public void Non_blank_components_are_not_normalized()
    {
        var key = new TransportDemandKey(" Wire_To_Gate ", " q-1 ");

        Assert.Equal(" Wire_To_Gate ", key.TaskType);
        Assert.Equal(" q-1 ", key.Sublot);
    }
}
