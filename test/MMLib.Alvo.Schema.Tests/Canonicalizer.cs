using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

// Canonical text for structural comparison: object members ordered ordinally by
// key (arrays left as-is, order is significant). F4 export order and schema-aware
// default-omission are separate, later concerns.
internal static class Canonicalizer
{
    internal static string Canonicalize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using var writer = new Utf8JsonWriter(buffer, options);
        Write(document.RootElement, writer);
        writer.Flush();

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty member in element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(member.Name);
                    Write(member.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
