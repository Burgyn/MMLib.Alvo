using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// A delivery's envelope as a fixture: the record images keep their keys, references and enum values, null values stay
/// null, every other value becomes <c>&lt;redacted:FIELD&gt;</c>, an application event's subject and partition-key value are
/// redacted, and <c>tracestate</c> is dropped (HX-06 Ruling 14, acceptance (5)).
/// </summary>
/// <remarks>
/// <para>
/// <b>Redaction is fail-closed on the shape this build writes: a value stays only when it is positively recognised as a
/// key.</b> A data event of an entity the current schema still declares keeps <c>id</c>, <c>tenant_id</c> and its <c>ref</c>
/// fields when the value is a uuid, and its <c>enum</c> fields when the value is one the schema lists; an application event,
/// or an entity the schema no longer declares, keeps only a uuid <c>id</c>. A <c>uuid</c> field is redacted — it may be a key
/// into another system or a person's identifier, and nothing in the schema says which — and a <c>json</c> field, an object or
/// an array is replaced whole.
/// </para>
/// <para>
/// <b>The envelope's attributes stay only on the shape this build writes them</b>, because they identify rather than
/// describe and a fixture must run as the event's tenant and actor: <c>id</c>, <c>tenantid</c>, <c>authid</c> and
/// <c>causationid</c> as uuids; <c>correlationid</c> as a uuid or a 32-hex-digit trace id; <c>traceparent</c> in the W3C
/// format; <c>authclaims</c> while it carries nothing but role names; the remaining standard attributes and Alvo extensions
/// as scalars. A data event's <c>subject</c> and <c>partitionkey</c> stay while they are the entity and a uuid; any other
/// partition key keeps its namespace only when that is the entity or <c>custom.event</c> (the marker
/// <c>IAlvoEvents.PublishAsync</c> writes), and is otherwise redacted whole. The changed-field names stay while each is a
/// field or managed column the declared entity has. An attribute this build does not know, and a member of <c>data</c>
/// other than the images and the changed-field names, is redacted whole.
/// </para>
/// <para>
/// <b>Deviation from the plan's Task 9 sketch</b>, which kept every attribute, every key field's value and anything under
/// <c>data</c> but the images: each of those is now kept only on the shape above, so an extension or a malformed key added
/// later cannot carry a value into a fixture. A payload that is not a JSON object is refused with a fixed message and no
/// inner exception, so the parser's own message cannot echo the payload.
/// </para>
/// <para>
/// <b>Placeholders are escaped in <see cref="JsonElement.GetRawText"/></b> (their angle brackets written as JSON unicode escapes), the default
/// encoder's choice; <see cref="JsonElement.GetString"/> reads them back unchanged. Whatever writes a fixture for a human to
/// read should serialize it with <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> at that point.
/// </para>
/// </remarks>
internal static class DeliveryFixtures
{
    private const string RecordMember = "record";
    private const string OldRecordMember = "old_record";
    private const string ChangedMember = "changed";
    private const string RolesClaim = "roles";
    private const string CustomPartitionMarker = "custom.event";
    private const char NamespaceSeparator = ':';
    private const char SubjectSeparator = '/';
    private const char TraceSeparator = '-';
    private const int TraceIdDigits = 32;
    private const string InvalidTraceVersion = "ff";

    private static readonly int[] _traceParentParts = [2, TraceIdDigits, 16, 2];

    private static readonly Dictionary<string, Func<JsonNode, bool>> _attributes = new(StringComparer.Ordinal)
    {
        [AlvoEventAttributes.SpecVersion] = IsScalar,
        [AlvoEventAttributes.Source] = IsScalar,
        [AlvoEventAttributes.Type] = IsScalar,
        [AlvoEventAttributes.Time] = IsScalar,
        [AlvoEventAttributes.DataContentType] = IsScalar,
        [AlvoEventAttributes.PayloadVersion] = IsScalar,
        [AlvoEventAttributes.ChainDepth] = IsScalar,
        [AlvoEventAttributes.AuthType] = IsScalar,
        [AlvoEventAttributes.Id] = value => Is(value, IsUuid),
        [AlvoEventAttributes.TenantId] = value => Is(value, IsUuid),
        [AlvoEventAttributes.AuthId] = value => Is(value, IsUuid),
        [AlvoEventAttributes.CausationId] = value => Is(value, IsUuid),
        [AlvoEventAttributes.CorrelationId] = value => Is(value, text => IsUuid(text) || IsHex(text, TraceIdDigits)),
        [AlvoEventAttributes.TraceParent] = value => Is(value, IsTraceParent),
        [AlvoEventAttributes.AuthClaims] = value => Is(value, IsRolesOnly),
    };

    private static readonly JsonDocumentOptions _strict = new() { AllowDuplicateProperties = false };

    /// <summary>The fixture of <paramref name="delivery"/>, redacted against <paramref name="schema"/>.</summary>
    /// <param name="delivery">The delivery the fixture is taken from.</param>
    /// <param name="payload">The stored envelope.</param>
    /// <param name="schema">The schema the instance runs now.</param>
    /// <exception cref="JsonException">The payload is not a JSON object; the message never repeats it.</exception>
    internal static DeliveryFixture Of(StoredDelivery delivery, string payload, SchemaModel schema)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(schema);
        var envelope = Parse(payload);
        var redaction = RedactionFor(DataEntity(envelope), schema);
        foreach (var attribute in envelope.Select(member => member.Key).ToList())
        {
            RedactAttribute(envelope, attribute, redaction);
        }

        return new DeliveryFixture
        {
            Subscriber = delivery.Subscriber,
            Delivery = delivery.Id,
            Attempt = Math.Max(1, delivery.Attempts),
            Envelope = ToElement(envelope),
            Redacted = redaction.Pointers,
        };
    }

    /// <summary>The placeholder a redacted value of <paramref name="field"/> becomes.</summary>
    /// <param name="field">The field's or attribute's name.</param>
    internal static string Placeholder(string field) => $"<redacted:{field}>";

    private static JsonObject Parse(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload, documentOptions: _strict) is JsonObject envelope)
            {
                return envelope;
            }
        }
        catch (JsonException)
        {
            // Rethrown below without the parser's message, which can quote the payload.
        }

        throw new JsonException("The stored envelope is not a JSON object, so no fixture can be taken from it.");
    }

    private static JsonElement ToElement(JsonObject envelope)
    {
        using var document = JsonDocument.Parse(envelope.ToJsonString());

        return document.RootElement.Clone();
    }

    private static string? DataEntity(JsonObject envelope) =>
        TextOf(envelope[AlvoEventAttributes.Type]) is { } name
        && EnvelopeReading.TryReadDataType(name, out var entity, out _)
        && entity.Length > 0
            ? entity
            : null;

    private static Redaction RedactionFor(string? entity, SchemaModel schema)
    {
        var kept = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal) { [AlvoManagedColumns.Id] = IsUuid };
        var declared = entity is null ? null : schema.Entities.FirstOrDefault(candidate => candidate.Name == entity);
        if (declared is null)
        {
            return new Redaction(entity, kept, null);
        }

        kept[AlvoManagedColumns.TenantId] = IsUuid;
        foreach (var field in declared.Fields)
        {
            AddKept(kept, field);
        }

        var names = new HashSet<string>(declared.Fields.Select(field => field.Name), StringComparer.Ordinal);
        names.UnionWith(AlvoManagedColumns.For(declared));

        return new Redaction(entity, kept, names);
    }

    private static void AddKept(Dictionary<string, Func<string, bool>> kept, FieldSchema field)
    {
        if (field.Type == FieldType.Ref)
        {
            kept[field.Name] = IsUuid;
        }
        else if (field.Type == FieldType.Enum && field.EnumValues is { } values)
        {
            kept[field.Name] = value => values.Contains(value, StringComparer.Ordinal);
        }
    }

    private static void RedactAttribute(JsonObject envelope, string attribute, Redaction redaction)
    {
        var value = envelope[attribute];
        switch (attribute)
        {
            case AlvoEventAttributes.Data: RedactData(envelope, redaction); break;
            case AlvoEventAttributes.TraceState: Drop(envelope, attribute, redaction); break;
            case AlvoEventAttributes.Subject: KeepWhen(envelope, attribute, value is null || IsDataKey(value, redaction.Entity, SubjectSeparator), redaction); break;
            case AlvoEventAttributes.PartitionKey: RedactPartitionKey(envelope, redaction); break;
            default: KeepWhen(envelope, attribute, value is null || (_attributes.TryGetValue(attribute, out var recognises) && recognises(value)), redaction); break;
        }
    }

    private static void RedactPartitionKey(JsonObject envelope, Redaction redaction)
    {
        var value = envelope[AlvoEventAttributes.PartitionKey];
        if (value is null || IsDataKey(value, redaction.Entity, NamespaceSeparator))
        {
            return;
        }

        var placeholder = Placeholder(AlvoEventAttributes.PartitionKey);
        var space = NamespaceOf(TextOf(value));
        var known = space is not null && (space == CustomPartitionMarker || space == redaction.Entity);
        envelope[AlvoEventAttributes.PartitionKey] = known ? $"{space}{NamespaceSeparator}{placeholder}" : placeholder;
        redaction.Pointers.Add(Pointer(AlvoEventAttributes.PartitionKey));
    }

    private static string? NamespaceOf(string? key)
    {
        var at = key?.IndexOf(NamespaceSeparator, StringComparison.Ordinal) ?? -1;

        return at > 0 ? key![..at] : null;
    }

    private static void RedactData(JsonObject envelope, Redaction redaction)
    {
        if (envelope[AlvoEventAttributes.Data] is not JsonObject data)
        {
            KeepWhen(envelope, AlvoEventAttributes.Data, envelope[AlvoEventAttributes.Data] is null, redaction);
            return;
        }

        var prefix = Pointer(AlvoEventAttributes.Data);
        foreach (var member in data.Select(member => member.Key).ToList())
        {
            if (member is RecordMember or OldRecordMember)
            {
                RedactImage(data, member, prefix, redaction);
            }
            else
            {
                KeepWhen(data, member, member == ChangedMember && IsNameList(data[member], redaction.Declared), redaction, prefix);
            }
        }
    }

    private static void RedactImage(JsonObject data, string image, string prefix, Redaction redaction)
    {
        if (data[image] is not JsonObject row)
        {
            KeepWhen(data, image, data[image] is null, redaction, prefix);
            return;
        }

        var rowPrefix = prefix + Pointer(image);
        foreach (var field in row.Select(member => member.Key).ToList())
        {
            KeepWhen(row, field, row[field] is null || IsKept(field, row[field], redaction), redaction, rowPrefix);
        }
    }

    private static bool IsKept(string field, JsonNode? value, Redaction redaction) =>
        redaction.Kept.TryGetValue(field, out var recognises) && TextOf(value) is { } text && recognises(text);

    private static void KeepWhen(JsonObject owner, string member, bool keep, Redaction redaction, string prefix = "")
    {
        if (!keep)
        {
            owner[member] = Placeholder(member);
            redaction.Pointers.Add(prefix + Pointer(member));
        }
    }

    private static void Drop(JsonObject owner, string member, Redaction redaction)
    {
        owner.Remove(member);
        redaction.Pointers.Add(Pointer(member));
    }

    private static bool IsDataKey(JsonNode? value, string? entity, char separator) =>
        entity is not null
        && TextOf(value) is { } text
        && text.StartsWith(entity + separator, StringComparison.Ordinal)
        && IsUuid(text[(entity.Length + 1)..]);

    /// <summary>Whether <paramref name="value"/> is an array of strings, each one of <paramref name="allowed"/> when that is given.</summary>
    private static bool IsNameList(JsonNode? value, IReadOnlySet<string>? allowed) =>
        value is JsonArray names
        && names.All(name => TextOf(name) is { } text && (allowed is null || allowed.Contains(text)));

    private static bool IsRolesOnly(string claims)
    {
        try
        {
            return JsonNode.Parse(claims, documentOptions: _strict) is JsonObject root
                && root.Count == 1
                && IsNameList(root[RolesClaim], null);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTraceParent(string text)
    {
        var parts = text.Split(TraceSeparator);

        return parts.Length == _traceParentParts.Length
            && parts.Zip(_traceParentParts).All(part => IsHex(part.First, part.Second))
            && parts[0] != InvalidTraceVersion;
    }

    private static bool IsHex(string text, int digits) =>
        text.Length == digits && text.All(char.IsAsciiHexDigitLower);

    private static bool IsScalar(JsonNode value) => value is JsonValue;

    private static bool Is(JsonNode value, Func<string, bool> recognises) => TextOf(value) is { } text && recognises(text);

    private static bool IsUuid(string text) => Guid.TryParse(text, out _);

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Pointer(string member) =>
        "/" + member.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>One fixture's redaction: the event's data entity, the fields it keeps and what was redacted.</summary>
    /// <param name="Entity">The data event's entity, or <see langword="null"/> for an application event.</param>
    /// <param name="Kept">The fields that may stay, each with what recognises a key value.</param>
    /// <param name="Declared">The declared entity's field and managed-column names, or <see langword="null"/> when none is declared.</param>
    private sealed record Redaction(string? Entity, Dictionary<string, Func<string, bool>> Kept, IReadOnlySet<string>? Declared)
    {
        public List<string> Pointers { get; } = [];
    }
}
