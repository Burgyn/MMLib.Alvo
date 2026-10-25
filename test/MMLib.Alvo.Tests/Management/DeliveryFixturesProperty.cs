using CsCheck;

using MMLib.Alvo.Events;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The property behind acceptance (5): for any record a schema may declare, of any field types and any value shapes — keys
/// that are not uuids, enum values the schema does not list, nested objects and arrays, attributes of the wrong shape, a
/// foreign subject or partition key, an extension or a data member this build never wrote, names that need pointer
/// escaping, on a declared, undeclared or application event — every value that is not a positively recognised key is the
/// placeholder, every listed pointer names one, and no original text survives.
/// </summary>
internal static class DeliveryFixturesProperty
{
    private const string Secret = "s3cr3t";
    private const string Entity = "tickets";
    private const string Retired = "retired";
    private const string Extension = "vendorext";
    private const string Marker = "custom.event";
    private const int Null = 0;
    private const int Valid = 1;
    private const int Uuid = 6;
    private const int EnumValue = 7;
    private const int Kinds = 8;

    private static readonly Guid _key = Guid.Parse("0192f1a4-0000-7000-8000-0000000000ff");
    private static readonly string[] _attributes =
        [AlvoEventAttributes.AuthId, AlvoEventAttributes.CausationId, AlvoEventAttributes.CorrelationId, AlvoEventAttributes.TraceParent];

    private static readonly Gen<Sample> _samples = Gen.Select(
            Gen.Select(
                Gen.Select(Gen.Enum<FieldType>(), Gen.Int[0, Kinds]).List[0, 10],
                Gen.Int[0, Kinds],
                Gen.Int[0, Kinds],
                Gen.Int[0, 2].Select(kind => (EventKind)kind),
                Gen.Bool),
            Gen.Select(
                Gen.Int[0, 3].Array[_attributes.Length],
                Gen.Int[0, 2],
                Gen.Int[0, 4],
                Gen.Int[0, Kinds],
                Gen.Int[0, 3]))
        .Select(pair => new Sample(
            pair.Item1.Item1, pair.Item1.Item2, pair.Item1.Item3, pair.Item1.Item4, pair.Item1.Item5,
            pair.Item2.Item1, pair.Item2.Item2, pair.Item2.Item3, pair.Item2.Item4, pair.Item2.Item5));

    private enum EventKind
    {
        Declared,
        Undeclared,
        Application,
    }

    /// <summary>Samples random payloads and checks each fixture.</summary>
    internal static void Holds(Guid tenant) => _samples.Sample(sample => Check(sample, tenant));

    private static void Check(Sample sample, Guid tenant)
    {
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope(sample, tenant), SchemaOf(sample));

        fixture.Envelope.GetRawText().ShouldNotContain(Secret);
        fixture.Redacted.Where(pointer => pointer != "/tracestate").ShouldAllBe(pointer => Resolve(fixture.Envelope, pointer).EndsWith('>'));
        CheckAttributes(fixture.Envelope, sample);
        CheckData(fixture.Envelope.GetProperty("data"), sample);
    }

    private static void CheckAttributes(JsonElement envelope, Sample sample)
    {
        ShouldBe(envelope, Extension, sample.Extra, Value(sample.Extra, Extension), kept: false);
        for (var index = 0; index < _attributes.Length; index++)
        {
            var kind = sample.Attributes[index];
            ShouldBe(envelope, _attributes[index], kind == Valid ? Uuid : kind, Attribute(index, kind), kept: kind == Valid);
        }

        var isData = sample.Event != EventKind.Application;
        ShouldBe(envelope, "subject", Uuid, Subject(sample), kept: isData && sample.Subject == 0);
        envelope.GetProperty("partitionkey").GetString().ShouldBe(ExpectedPartition(sample));
    }

    private static string ExpectedPartition(Sample sample)
    {
        var placeholder = DeliveryFixtures.Placeholder("partitionkey");
        var original = Partition(sample);
        if (sample.Event != EventKind.Application && sample.Partition == 0)
        {
            return original;
        }

        var space = original.Contains(':', StringComparison.Ordinal) ? original[..original.IndexOf(':', StringComparison.Ordinal)] : null;
        var known = space == Marker || (sample.Event != EventKind.Application && space == EntityOf(sample));

        return known ? $"{space}:{placeholder}" : placeholder;
    }

    private static void CheckData(JsonElement data, Sample sample)
    {
        var changedKept = sample.Event != EventKind.Declared || sample.Changed == 0;
        data.GetProperty("changed").ValueKind.ShouldBe(changedKept ? JsonValueKind.Array : JsonValueKind.String);
        if (sample.Changed == 2)
        {
            data.GetProperty("note").GetString().ShouldBe(DeliveryFixtures.Placeholder("note"));
        }

        foreach (var image in new[] { "record", "old_record" })
        {
            CheckImage(data.GetProperty(image), sample);
        }
    }

    private static void CheckImage(JsonElement row, Sample sample)
    {
        var declared = sample.Event == EventKind.Declared;
        ShouldBe(row, "id", sample.Id, Value(sample.Id, "id"), kept: sample.Id == Uuid);
        ShouldBe(row, "tenant_id", sample.Tenant, Value(sample.Tenant, "tenant_id"), kept: declared && sample.Tenant == Uuid);
        for (var index = 0; index < sample.Fields.Count; index++)
        {
            var (fieldType, kind) = sample.Fields[index];
            var name = NameOf(sample, index);
            ShouldBe(row, name, kind, Value(kind, name), kept: declared && IsKey(fieldType, kind));
        }
    }

    private static bool IsKey(FieldType type, int kind) =>
        (type == FieldType.Ref && kind == Uuid) || (type == FieldType.Enum && kind == EnumValue);

    private static void ShouldBe(JsonElement owner, string member, int kind, JsonNode? original, bool kept)
    {
        var value = owner.GetProperty(member);
        if (kind == Null)
        {
            value.ValueKind.ShouldBe(JsonValueKind.Null, member);
        }
        else if (kept)
        {
            JsonElement.DeepEquals(value, JsonSerializer.SerializeToElement(original)).ShouldBeTrue(member);
        }
        else
        {
            value.GetString().ShouldBe(DeliveryFixtures.Placeholder(member));
        }
    }

    private static string Resolve(JsonElement root, string pointer)
    {
        var node = root;
        foreach (var segment in pointer[1..].Split('/'))
        {
            node = node.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        }

        return node.GetString()!;
    }

    private static string NameOf(Sample sample, int index) => sample.OddNames ? $"f{index}/a~b" : $"f{index}";

    private static string EntityOf(Sample sample) => sample.Event switch
    {
        EventKind.Declared => Entity,
        EventKind.Undeclared => Retired,
        _ => "orders",
    };

    private static string TypeOf(Sample sample) => sample.Event switch
    {
        EventKind.Declared => $"entity.{Entity}.updated",
        EventKind.Undeclared => $"entity.{Retired}.updated",
        _ => "orders.shipped",
    };

    private static string Subject(Sample sample) => sample.Subject switch
    {
        0 => $"{EntityOf(sample)}/{_key}",
        1 => $"{EntityOf(sample)}/{Secret}",
        _ => Secret,
    };

    private static string Partition(Sample sample) => sample.Partition switch
    {
        0 when sample.Event == EventKind.Application => $"{Marker}:orders/{_key}",
        0 => $"{EntityOf(sample)}:{_key}",
        1 => $"{EntityOf(sample)}:{Secret}",
        2 => $"{Marker}:{Secret}",
        3 => $"other.ns:{Secret}",
        _ => Secret,
    };

    private static JsonNode? Attribute(int index, int kind) => kind switch
    {
        Null => null,
        Valid when _attributes[index] == AlvoEventAttributes.CorrelationId => JsonValue.Create("4bf92f3577b34da6a3ce929d0e0e4736"),
        Valid when _attributes[index] == AlvoEventAttributes.TraceParent => JsonValue.Create("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"),
        Valid => JsonValue.Create(_key.ToString()),
        2 => JsonValue.Create($"{Secret}-{_attributes[index]}"),
        _ => new JsonObject { ["inner"] = Secret },
    };

    private static SchemaModel SchemaOf(Sample sample) => new([new EntitySchema
    {
        Name = Entity,
        Fields =
        [
            .. sample.Fields.Select((field, index) => new FieldSchema
            {
                Name = NameOf(sample, index),
                Type = field.Type,
                EnumValues = field.Type == FieldType.Enum ? ["open", "closed"] : null,
            }),
        ],
    }]);

    private static string Envelope(Sample sample, Guid tenant)
    {
        var envelope = new JsonObject
        {
            [AlvoEventAttributes.SpecVersion] = AlvoEvent.SpecVersion,
            [AlvoEventAttributes.Id] = _key.ToString(),
            [AlvoEventAttributes.Source] = AlvoEvent.DefaultSource,
            [AlvoEventAttributes.Type] = TypeOf(sample),
            [AlvoEventAttributes.Subject] = Subject(sample),
            [AlvoEventAttributes.PartitionKey] = Partition(sample),
            [AlvoEventAttributes.TenantId] = tenant.ToString(),
            [AlvoEventAttributes.TraceState] = $"vendor={Secret}",
            [Extension] = Value(sample.Extra, Extension),
            [AlvoEventAttributes.Data] = Data(sample),
        };
        for (var index = 0; index < _attributes.Length; index++)
        {
            envelope[_attributes[index]] = Attribute(index, sample.Attributes[index]);
        }

        return envelope.ToJsonString();
    }

    private static JsonObject Data(Sample sample)
    {
        var data = new JsonObject
        {
            ["record"] = Row(sample),
            ["old_record"] = Row(sample),
            ["changed"] = new JsonArray(sample.Changed == 0 ? "id" : "ghost"),
        };
        if (sample.Changed == 2)
        {
            data["note"] = $"{Secret}-note";
        }

        return data;
    }

    private static JsonObject Row(Sample sample)
    {
        var row = new JsonObject { ["id"] = Value(sample.Id, "id"), ["tenant_id"] = Value(sample.Tenant, "tenant_id") };
        for (var index = 0; index < sample.Fields.Count; index++)
        {
            row[NameOf(sample, index)] = Value(sample.Fields[index].Kind, NameOf(sample, index));
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

    private sealed record Sample(
        List<(FieldType Type, int Kind)> Fields,
        int Id,
        int Tenant,
        EventKind Event,
        bool OddNames,
        int[] Attributes,
        int Subject,
        int Partition,
        int Extra,
        int Changed);
}
