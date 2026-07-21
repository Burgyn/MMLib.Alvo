using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

// F2 canonical form: deterministic member order (ordinal by key) and stable
// indentation. Structural equality of two descriptors = equality of their
// canonical text. Schema-aware default-omission is deferred to F4 export.
internal static class Canonicalizer
{
    internal static string Canonicalize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            Write(document.RootElement, writer);
        }

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
