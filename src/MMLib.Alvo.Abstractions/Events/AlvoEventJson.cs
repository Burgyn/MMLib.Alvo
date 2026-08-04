using MMLib.Alvo.Data;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Events;

/// <summary>
/// Writes and reads an <see cref="AlvoEvent"/> in the CloudEvents JSON format — the bytes that go into the
/// outbox row and out over a webhook.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written over <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/> rather than delegated to
/// <c>JsonSerializer</c>, for two reasons the shape makes unavoidable: extensions are <b>flat top-level</b>
/// members, so the envelope's own JSON has no nesting a POCO could express, and an absent optional
/// attribute must be <em>absent</em> rather than <c>null</c>, which CloudEvents requires and a serializer
/// setting only approximates. Every name comes from <see cref="AlvoEventAttributes"/>, never from a literal.
/// </para>
/// <para>
/// <b>The default HTML-safe encoder is kept deliberately.</b> An event payload is POSTed to a webhook and
/// read back into dashboards, so a field value that could close an HTML context is escaped on the wire
/// rather than trusted to be escaped by whoever renders it — secure-by-default, at the cost of a few bytes
/// per event and a <c>+</c> spelled <c>+</c> inside timestamps. The escaping is lossless, which
/// <c>A_value_that_could_close_an_html_context_is_escaped_and_still_round_trips</c> pins, so switching to a
/// relaxed encoder would buy readability and nothing else.
/// </para>
/// <para>
/// <b>What <see cref="Read"/> returns is JSON's view of a row, not the row's own types.</b> JSON carries no
/// CLR type, so a <see cref="Guid"/> field reads back as its text and a <see cref="decimal"/> as the
/// narrowest numeric type that holds it. That is a decision, not an oversight: the read side's consumer is
/// the dispatcher, which evaluates conditions and renders templates over the textual view anyway, while the
/// authoritative typed record lives on the write path, where the schema is in scope.
/// </para>
/// </remarks>
public static class AlvoEventJson
{
    private const string RecordMember = "record";
    private const string OldRecordMember = "old_record";
    private const string ChangedMember = "changed";
    private const string RoundTripFormat = "O";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Writes <paramref name="event"/> as a CloudEvents JSON document.</summary>
    /// <param name="event">The event to write.</param>
    /// <exception cref="NotSupportedException">A field carries a value this format does not know how to write.</exception>
    public static string Write(AlvoEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteStandardAttributes(writer, @event);
            WriteExtensions(writer, @event);
            WriteData(writer, @event.Data);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Reads an <see cref="AlvoEvent"/> from a CloudEvents JSON document.</summary>
    /// <param name="json">The document, as written by <see cref="Write"/>.</param>
    /// <exception cref="JsonException">
    /// The document is not an Alvo envelope: a required attribute is missing, it carries the wrong JSON
    /// type, or its <c>specversion</c> is not the one this build writes.
    /// </exception>
    public static AlvoEvent Read(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        EnsureSupportedSpecVersion(root);

        return ReadEnvelope(root);
    }

    private static void WriteStandardAttributes(Utf8JsonWriter writer, AlvoEvent @event)
    {
        writer.WriteString(AlvoEventAttributes.SpecVersion, AlvoEvent.SpecVersion);
        writer.WriteString(AlvoEventAttributes.Id, @event.Id);
        writer.WriteString(AlvoEventAttributes.Source, @event.Source);
        writer.WriteString(AlvoEventAttributes.Type, @event.Type);
        writer.WriteString(
            AlvoEventAttributes.Time, @event.Time.ToString(RoundTripFormat, CultureInfo.InvariantCulture));
        writer.WriteString(AlvoEventAttributes.Subject, @event.Subject);
        writer.WriteString(AlvoEventAttributes.DataContentType, AlvoEvent.DataContentType);
    }

    private static void WriteExtensions(Utf8JsonWriter writer, AlvoEvent @event)
    {
        writer.WriteString(AlvoEventAttributes.PartitionKey, @event.PartitionKey);
        writer.WriteNumber(AlvoEventAttributes.PayloadVersion, @event.PayloadVersion);
        writer.WriteNumber(AlvoEventAttributes.ChainDepth, @event.ChainDepth);
        writer.WriteString(AlvoEventAttributes.AuthType, @event.AuthType);
        WriteOptional(writer, AlvoEventAttributes.AuthId, @event.AuthId);
        writer.WriteString(AlvoEventAttributes.CorrelationId, @event.CorrelationId);
        WriteOptional(writer, AlvoEventAttributes.CausationId, @event.CausationId);
    }

    private static void WriteOptional(Utf8JsonWriter writer, string attribute, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(attribute, value);
        }
    }

    private static void WriteData(Utf8JsonWriter writer, AlvoEventData data)
    {
        writer.WriteStartObject(AlvoEventAttributes.Data);
        WriteRecord(writer, RecordMember, data.Record);
        WriteRecord(writer, OldRecordMember, data.OldRecord);

        writer.WriteStartArray(ChangedMember);
        foreach (var field in data.Changed)
        {
            writer.WriteStringValue(field);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRecord(Utf8JsonWriter writer, string member, AlvoRecord? record)
    {
        if (record is null)
        {
            return;
        }

        writer.WriteStartObject(member);
        foreach (var field in record.Values)
        {
            WriteValue(writer, field.Key, record[field.Key]);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, string field, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(field); break;
            case string text: writer.WriteString(field, text); break;
            case bool flag: writer.WriteBoolean(field, flag); break;
            case Guid id: writer.WriteString(field, id); break;
            case DateTimeOffset moment:
                writer.WriteString(field, moment.ToString(RoundTripFormat, CultureInfo.InvariantCulture));
                break;
            case DateTime moment:
                writer.WriteString(field, moment.ToString(RoundTripFormat, CultureInfo.InvariantCulture));
                break;
            case DateOnly day:
                writer.WriteString(field, day.ToString(DateFormat, CultureInfo.InvariantCulture));
                break;
            case decimal amount: writer.WriteNumber(field, amount); break;
            case ulong count: writer.WriteNumber(field, count); break;
            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumber(field, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case float or double:
                writer.WriteNumber(field, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            default: throw UnwritableValue(field, value);
        }
    }

    private static NotSupportedException UnwritableValue(string field, object value) =>
        new($"Field '{field}' carries a {value.GetType().Name}, which an event payload cannot express. "
            + "Convert it to one of the field types the schema allows (text, uuid, number, decimal, "
            + "boolean, date or timestamp) before emitting the event.");

    private static void EnsureSupportedSpecVersion(JsonElement root)
    {
        var specVersion = RequiredString(root, AlvoEventAttributes.SpecVersion);
        if (specVersion != AlvoEvent.SpecVersion)
        {
            throw new JsonException(
                $"'{AlvoEventAttributes.SpecVersion}' is '{specVersion}', and this build reads "
                + $"'{AlvoEvent.SpecVersion}' only.");
        }
    }

    private static AlvoEvent ReadEnvelope(JsonElement root) => new()
    {
        Id = Guid.Parse(RequiredString(root, AlvoEventAttributes.Id), CultureInfo.InvariantCulture),
        Source = RequiredString(root, AlvoEventAttributes.Source),
        Type = RequiredString(root, AlvoEventAttributes.Type),
        Time = DateTimeOffset.Parse(
            RequiredString(root, AlvoEventAttributes.Time),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind),
        Subject = RequiredString(root, AlvoEventAttributes.Subject),
        PartitionKey = RequiredString(root, AlvoEventAttributes.PartitionKey),
        AuthType = RequiredString(root, AlvoEventAttributes.AuthType),
        CorrelationId = RequiredString(root, AlvoEventAttributes.CorrelationId),
        PayloadVersion = RequiredInt32(root, AlvoEventAttributes.PayloadVersion),
        ChainDepth = RequiredInt32(root, AlvoEventAttributes.ChainDepth),
        AuthId = OptionalString(root, AlvoEventAttributes.AuthId),
        CausationId = OptionalString(root, AlvoEventAttributes.CausationId),
        Data = ReadData(root),
    };

    private static AlvoEventData ReadData(JsonElement root)
    {
        var data = Required(root, AlvoEventAttributes.Data);

        return new AlvoEventData
        {
            Record = ReadRecord(data, RecordMember),
            OldRecord = ReadRecord(data, OldRecordMember),
            Changed = ReadChanged(data),
        };
    }

    private static AlvoRecord? ReadRecord(JsonElement data, string member)
    {
        if (!data.TryGetProperty(member, out var record))
        {
            return null;
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in record.EnumerateObject())
        {
            values[field.Name] = ReadValue(field.Name, field.Value);
        }

        return new AlvoRecord(values);
    }

    private static IReadOnlyList<string> ReadChanged(JsonElement data) =>
        data.TryGetProperty(ChangedMember, out var changed)
            ? [.. changed.EnumerateArray().Select(field => field.GetString() ?? string.Empty)]
            : [];

    private static object? ReadValue(string field, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => ReadNumber(value),
        _ => throw new JsonException(
            $"Field '{field}' carries a {value.ValueKind} value, which no field type maps to."),
    };

    private static object ReadNumber(JsonElement value) =>
        value.TryGetInt64(out var whole) ? whole
        : value.TryGetDecimal(out var exact) ? exact
        : value.GetDouble();

    private static JsonElement Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value
            : throw new JsonException($"'{name}' is required on an Alvo event envelope and is missing.");

    private static string RequiredString(JsonElement root, string name) =>
        Required(root, name).GetString()
        ?? throw new JsonException($"'{name}' is required on an Alvo event envelope and is null.");

    private static int RequiredInt32(JsonElement root, string name) => Required(root, name).GetInt32();

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;
}
