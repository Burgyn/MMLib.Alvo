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
/// <b>Redaction is fail-closed: a value stays only when it is positively recognised as a key.</b> A data event of an entity
/// the current schema still declares keeps <c>id</c>, <c>tenant_id</c> and its <c>ref</c> fields when the value is a uuid, and
/// its <c>enum</c> fields when the value is one the schema lists; an application event, or an entity the schema no longer
/// declares, keeps only a uuid <c>id</c>. A <c>uuid</c> field is redacted — it may be a key into another system or a person's
/// identifier, and nothing in the schema says which — and a <c>json</c> field, an object or an array is replaced whole.
/// </para>
/// <para>
/// <b>The envelope's attributes stay when this build wrote them as scalars</b> — ids, type, time, tenant, <c>authid</c>,
/// chain depth — because they identify rather than describe and a fixture must run as the event's tenant and actor.
/// <c>authclaims</c> stays only while it carries nothing but role names. A data event's <c>subject</c> and
/// <c>partitionkey</c> stay only while they are the entity and a uuid. An attribute this build does not know, and a member
/// of <c>data</c> other than the images and the changed-field names, is redacted whole.
/// </para>
/// <para>
/// <b>Deviation from the plan's Task 9 sketch</b>, which kept every attribute, every key field's value and anything under
/// <c>data</c> but the images: each of those is now kept only on the shape above, so an extension or a malformed key added
/// later cannot carry a value into a fixture. A payload that is not a JSON object is refused with a fixed message and no
/// inner exception, so the parser's own message cannot echo the payload.
/// </para>
/// </remarks>
internal static class DeliveryFixtures
{
    private const string RecordMember = "record";
    private const string OldRecordMember = "old_record";
    private const string ChangedMember = "changed";
    private const string RolesClaim = "roles";
    private const char NamespaceSeparator = ':';
    private const char SubjectSeparator = '/';

    private static readonly HashSet<string> _scalarAttributes = new(StringComparer.Ordinal)
    {
        AlvoEventAttributes.SpecVersion, AlvoEventAttributes.Id, AlvoEventAttributes.Source, AlvoEventAttributes.Type,
        AlvoEventAttributes.Time, AlvoEventAttributes.DataContentType, AlvoEventAttributes.PayloadVersion,
        AlvoEventAttributes.ChainDepth, AlvoEventAttributes.AuthType, AlvoEventAttributes.AuthId,
        AlvoEventAttributes.CorrelationId, AlvoEventAttributes.CausationId, AlvoEventAttributes.TenantId,
        AlvoEventAttributes.TraceParent,
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
        var entity = DataEntity(envelope);
        var redaction = new Redaction(entity, KeptFields(entity, schema));
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

    private static Dictionary<string, Func<string, bool>> KeptFields(string? entity, SchemaModel schema)
    {
        var kept = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal) { [AlvoManagedColumns.Id] = IsUuid };
        if (entity is not null && schema.Entities.FirstOrDefault(candidate => candidate.Name == entity) is { } declared)
        {
            kept[AlvoManagedColumns.TenantId] = IsUuid;
            foreach (var field in declared.Fields)
            {
                AddKept(kept, field);
            }
        }

        return kept;
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
            case AlvoEventAttributes.Subject: KeepWhen(envelope, attribute, IsDataKey(value, redaction.Entity, SubjectSeparator), redaction); break;
            case AlvoEventAttributes.PartitionKey: RedactPartitionKey(envelope, redaction); break;
            case AlvoEventAttributes.AuthClaims: KeepWhen(envelope, attribute, value is null || IsRolesOnly(TextOf(value)), redaction); break;
            default: KeepWhen(envelope, attribute, value is null || (_scalarAttributes.Contains(attribute) && value is JsonValue), redaction); break;
        }
    }

    private static void RedactPartitionKey(JsonObject envelope, Redaction redaction)
    {
        var value = envelope[AlvoEventAttributes.PartitionKey];
        if (value is null || IsDataKey(value, redaction.Entity, NamespaceSeparator))
        {
            return;
        }

        var text = TextOf(value);
        var at = text?.IndexOf(NamespaceSeparator, StringComparison.Ordinal) ?? -1;
        var placeholder = Placeholder(AlvoEventAttributes.PartitionKey);
        envelope[AlvoEventAttributes.PartitionKey] = at > 0 ? string.Concat(text.AsSpan(0, at + 1), placeholder) : placeholder;
        redaction.Pointers.Add(Pointer(AlvoEventAttributes.PartitionKey));
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
                KeepWhen(data, member, member == ChangedMember && IsNameList(data[member]), redaction, prefix);
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

    private static bool IsNameList(JsonNode? value) =>
        value is JsonArray names && names.All(name => TextOf(name) is not null);

    private static bool IsRolesOnly(string? claims)
    {
        if (claims is null)
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(claims, documentOptions: _strict) is JsonObject root
                && root.Count == 1
                && IsNameList(root[RolesClaim]);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsUuid(string text) => Guid.TryParse(text, out _);

    private static string? TextOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Pointer(string member) =>
        "/" + member.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>One fixture's redaction: the event's data entity, the fields it keeps and what was redacted.</summary>
    /// <param name="Entity">The data event's entity, or <see langword="null"/> for an application event.</param>
    /// <param name="Kept">The fields that may stay, each with what recognises a key value.</param>
    private sealed record Redaction(string? Entity, Dictionary<string, Func<string, bool>> Kept)
    {
        public List<string> Pointers { get; } = [];
    }
}
