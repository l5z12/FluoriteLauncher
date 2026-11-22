using System.Text.Json;
using System.Text.Json.Serialization;
using Fluorite.Core.Json;

namespace Fluorite.Core.Versions;

internal sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ArgumentEntry([reader.GetString()!]);

            case JsonTokenType.StartObject:
                List<Rule>? rules = null;
                List<string> values = [];
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("Expected property name in argument entry.");
                    var name = reader.GetString();
                    reader.Read();
                    switch (name)
                    {
                        case "rules":
                            rules = JsonSerializer.Deserialize(ref reader, FluoriteJsonContext.Default.ListRule);
                            break;
                        case "value":
                        case "values":
                            ReadValues(ref reader, values);
                            break;
                        default:
                            reader.Skip();
                            break;
                    }
                }
                return new ArgumentEntry(values, rules);

            case JsonTokenType.Null:
                return null;

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} in argument list.");
        }
    }

    private static void ReadValues(ref Utf8JsonReader reader, List<string> values)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            values.Add(reader.GetString()!);
            return;
        }
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Argument value must be a string or array of strings.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            values.Add(reader.GetString() ?? "");
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
    {
        if (value.Rules is null && value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
            return;
        }
        writer.WriteStartObject();
        if (value.Rules is not null)
        {
            writer.WritePropertyName("rules");
            JsonSerializer.Serialize(writer, value.Rules, FluoriteJsonContext.Default.ListRule);
        }
        writer.WritePropertyName("value");
        if (value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
        }
        else
        {
            writer.WriteStartArray();
            foreach (var v in value.Values)
                writer.WriteStringValue(v);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
