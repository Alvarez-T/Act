using Act.Utils;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YFex.Json;

public sealed class PercentualJsonConverter : JsonConverter<Percentual>
{
    public override Percentual Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => Percentual.FromPercentage(reader.GetDecimal()),
            JsonTokenType.String => Percentual.Parse(reader.GetString()!),
            _ => throw new JsonException($"Cannot deserialize token {reader.TokenType} as Percentual.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Percentual value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.ToPercentageValue());
}