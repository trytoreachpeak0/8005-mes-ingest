using System.Text.Json;
using System.Text.Json.Serialization;

namespace MesIngest.Core.SeriesProjection;

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
            throw new JsonException("The HistoryEpoch token value is invalid.");
        }

        return HistoryEpoch.FromGuid(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        HistoryEpoch value,
        JsonSerializerOptions options) => writer.WriteStringValue(value.Value.ToString("D"));
}
