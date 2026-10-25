using CsCheck;

using MMLib.Alvo.Events;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The property behind acceptance (5): for any record a schema may declare, of any field types and any value shapes — keys
/// that are not uuids, enum values the schema does not list, nested objects and arrays, an extension this build never wrote —
/// every value that is not a positively recognised key is the placeholder, and no original text survives.
/// </summary>
internal static class DeliveryFixturesProperty
{
    private const string Secret = "s3cr3t";
    private const string Entity = "tickets";
    private const string Extension = "vendorext";
    private const int Null = 0;
    private const int Uuid = 6;
    private const int EnumValue = 7;
    private const int Kinds = 8;

    private static readonly Guid _key = Guid.Parse("0192f1a4-0000-7000-8000-0000000000ff");

    private static readonly Gen<(List<(FieldType Type, int Kind)> Fields, int Id, int Tenant, bool Declared, int Extra)> _samples =
        Gen.Select(
            Gen.Select(Gen.Enum<FieldType>(), Gen.Int[0, Kinds]).List[0, 12],
            Gen.Int[0, Kinds],
            Gen.Int[0, Kinds],
            Gen.Bool,
            Gen.Int[0, Kinds]);

    /// <summary>Samples random payloads and checks each fixture.</summary>
    internal static void Holds(Guid tenant) => _samples.Sample(sample => Check(sample, tenant));

    private static void Check((List<(FieldType Type, int Kind)> Fields, int Id, int Tenant, bool Declared, int Extra) sample, Guid tenant)
    {
        var schema = SchemaOf(sample.Fields);
        var type = sample.Declared ? $"entity.{Entity}.updated" : "entity.retired.updated";
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope(type, sample, tenant), schema);

        fixture.Envelope.GetRawText().ShouldNotContain(Secret);
        ShouldBe(fixture.Envelope, Extension, sample.Extra, kept: false);
        foreach (var image in new[] { "record", "old_record" })
        {
            var row = fixture.Envelope.GetProperty("data").GetProperty(image);
            ShouldBe(row, "id", sample.Id, kept: sample.Id == Uuid);
            ShouldBe(row, "tenant_id", sample.Tenant, kept: sample.Declared && sample.Tenant == Uuid);
            for (var index = 0; index < sample.Fields.Count; index++)
            {
                var (fieldType, kind) = sample.Fields[index];
                ShouldBe(row, $"f{index}", kind, kept: sample.Declared && IsKey(fieldType, kind));
            }
        }
    }

    private static bool IsKey(FieldType type, int kind) =>
        (type == FieldType.Ref && kind == Uuid) || (type == FieldType.Enum && kind == EnumValue);

    private static void ShouldBe(JsonElement row, string field, int kind, bool kept)
    {
        var value = row.GetProperty(field);
        if (kind == Null)
        {
            value.ValueKind.ShouldBe(JsonValueKind.Null, field);
        }
        else if (kept)
        {
            JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(Value(kind, field))).ShouldBeTrue(field);
        }
        else
        {
            value.GetString().ShouldBe(DeliveryFixtures.Placeholder(field));
        }
    }

    private static SchemaModel SchemaOf(List<(FieldType Type, int Kind)> fields) => new([new EntitySchema
    {
        Name = Entity,
        Fields =
        [
            .. fields.Select((field, index) => new FieldSchema
            {
                Name = $"f{index}",
                Type = field.Type,
                EnumValues = field.Type == FieldType.Enum ? ["open", "closed"] : null,
            }),
        ],
    }]);

    private static string Envelope(string type, (List<(FieldType Type, int Kind)> Fields, int Id, int Tenant, bool Declared, int Extra) sample, Guid tenant)
    {
        var envelope = new JsonObject
        {
            [AlvoEventAttributes.SpecVersion] = AlvoEvent.SpecVersion,
            [AlvoEventAttributes.Id] = _key.ToString(),
            [AlvoEventAttributes.Source] = AlvoEvent.DefaultSource,
            [AlvoEventAttributes.Type] = type,
            [AlvoEventAttributes.Subject] = $"{Entity}/{_key}",
            [AlvoEventAttributes.PartitionKey] = $"{Entity}:{_key}",
            [AlvoEventAttributes.TenantId] = tenant.ToString(),
            [Extension] = Value(sample.Extra, Extension),
            [AlvoEventAttributes.Data] = new JsonObject
            {
                ["record"] = Row(sample),
                ["old_record"] = Row(sample),
                ["changed"] = new JsonArray("f0"),
            },
        };

        return envelope.ToJsonString();
    }

    private static JsonObject Row((List<(FieldType Type, int Kind)> Fields, int Id, int Tenant, bool Declared, int Extra) sample)
    {
        var row = new JsonObject { ["id"] = Value(sample.Id, "id"), ["tenant_id"] = Value(sample.Tenant, "tenant_id") };
        for (var index = 0; index < sample.Fields.Count; index++)
        {
            row[$"f{index}"] = Value(sample.Fields[index].Kind, $"f{index}");
        }

        return row;
    }

    private static JsonNode? Value(int kind, string field) => kind switch
    {
        Null => null,
        1 => JsonValue.Create($"{Secret}-{field}"),
        2 => JsonValue.Create(4242.5m),
        3 => JsonValue.Create(true),
        4 => new JsonObject { ["inner"] = $"{Secret}-{field}" },
        5 => new JsonArray($"{Secret}-{field}", 7),
        Uuid => JsonValue.Create(_key.ToString()),
        EnumValue => JsonValue.Create("open"),
        _ => JsonValue.Create(string.Empty),
    };

    private static StoredDelivery Delivery() => new()
    {
        Id = _key,
        EventId = _key,
        Subscriber = "send-confirmation",
        PartitionKey = $"{Entity}:{_key}",
        Kind = "handler",
        Status = "dead",
        Attempts = 0,
        NotBefore = DateTimeOffset.UnixEpoch,
    };
}
