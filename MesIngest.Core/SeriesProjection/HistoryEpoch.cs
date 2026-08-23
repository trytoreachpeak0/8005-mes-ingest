using System.Text.Json;
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

internal sealed class HistoryEpochJsonConverter : JsonConverter<HistoryEpoch>
{
    public override HistoryEpoch Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!Guid.TryParseExact(text, "D", out var value) || value == Guid.Empty)
        {
            throw new JsonException("The HistoryEpoch value is invalid.");
        }

        return HistoryEpoch.FromGuid(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        HistoryEpoch value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString("D"));
}
