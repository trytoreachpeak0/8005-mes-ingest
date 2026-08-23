using System.Text.Json.Serialization;

namespace MesIngest.Core.SeriesProjection;

/// <summary>
/// Stable identity of one continuous, traceable database history.
/// </summary>
[JsonConverter(typeof(HistoryEpochJsonConverter))]
public sealed record HistoryEpoch
{
    private HistoryEpoch(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException(
                "A HistoryEpoch must contain a non-empty GUID.",
                nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static HistoryEpoch CreateNew() => new(Guid.NewGuid());

    public static HistoryEpoch FromGuid(Guid value) => new(value);

    public override string ToString() => Value.ToString("D");
}
