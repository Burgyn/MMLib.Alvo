using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The text a timer stores its <see cref="AlvoProvenance"/> as, and back — so the event it fires later resumes the flow, and
/// the chain depth, of the code that armed it.
/// </summary>
internal static class TimerProvenance
{
    private const string CorrelationId = "correlationId";
    private const string CausationId = "causationId";
    private const string Depth = "depth";
    private const string TraceParent = "traceParent";
    private const string TraceState = "traceState";

    /// <summary>Writes <paramref name="provenance"/> as a flat JSON object, omitting what is unknown.</summary>
    /// <param name="provenance">The provenance.</param>
    /// <returns>The JSON text.</returns>
    internal static string Write(AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteOptional(writer, CorrelationId, provenance.CorrelationId);
            WriteOptional(writer, CausationId, provenance.CausationId);
            writer.WriteNumber(Depth, provenance.Depth);
            WriteOptional(writer, TraceParent, provenance.TraceParent);
            WriteOptional(writer, TraceState, provenance.TraceState);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads what <see cref="Write"/> wrote; anything malformed reads as <see cref="AlvoProvenance.None"/>.</summary>
    /// <param name="json">The stored text.</param>
    /// <returns>The provenance.</returns>
    internal static AlvoProvenance Read(string json) => TryRead(json) ?? AlvoProvenance.None;

    /// <summary>Reads what <see cref="Write"/> wrote, strictly: anything malformed reads as <see langword="null"/>.</summary>
    /// <param name="json">The stored text.</param>
    /// <returns>The provenance, or <see langword="null"/> when the text is not one — the timer pump's sign of a corrupted row.</returns>
    internal static AlvoProvenance? TryRead(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadObject(document.RootElement);
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static AlvoProvenance ReadObject(JsonElement root) => new()
    {
        CorrelationId = Optional(root, CorrelationId),
        CausationId = Optional(root, CausationId),
        Depth = root.GetProperty(Depth).GetInt32(),
        TraceParent = Optional(root, TraceParent),
        TraceState = Optional(root, TraceState),
    };

    private static string? Optional(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
