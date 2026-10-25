using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Tests.Management;

/// <summary>A fixture contains no non-key scalar (spec 2026-10-14 HX-06 acceptance (5)): keys, references and enums stay, every other value is redacted.</summary>
public sealed class DeliveryFixturesTests
{
    private static readonly Guid _ticket = Guid.Parse("0192f1a4-0000-7000-8000-00000000000a");
    private static readonly Guid _customer = Guid.Parse("0192f1a4-0000-7000-8000-00000000000b");
    private static readonly Guid _tenant = Guid.Parse("0192f1a4-0000-7000-8000-00000000000c");

    private static readonly SchemaModel _schema = new([new EntitySchema
    {
        Name = "tickets",
        Fields =
        [
            new FieldSchema { Name = "title", Type = FieldType.String },
            new FieldSchema { Name = "total", Type = FieldType.Decimal },
            new FieldSchema { Name = "is_urgent", Type = FieldType.Boolean },
            new FieldSchema { Name = "external_ref", Type = FieldType.Uuid },
            new FieldSchema { Name = "metadata", Type = FieldType.Json },
            new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["open", "closed"] },
            new FieldSchema { Name = "customer_id", Type = FieldType.Ref },
            new FieldSchema { Name = "note", Type = FieldType.Text },
        ],
    }]);

    [Fact]
    public void A_fixture_keeps_keys_references_and_enums_and_redacts_every_other_value()
    {
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), _schema);

        var record = fixture.Envelope.GetProperty("data").GetProperty("record");
        record.GetProperty("id").GetString().ShouldBe(_ticket.ToString());
        record.GetProperty("tenant_id").GetString().ShouldBe(_tenant.ToString());
        record.GetProperty("customer_id").GetString().ShouldBe(_customer.ToString());
        record.GetProperty("status").GetString().ShouldBe("open");
        foreach (var field in new[] { "title", "total", "is_urgent", "external_ref", "metadata" })
        {
            record.GetProperty(field).GetString().ShouldBe($"<redacted:{field}>");
        }

        record.GetProperty("note").ValueKind.ShouldBe(JsonValueKind.Null, "an empty value carries nothing to hide and stays empty");
        fixture.Redacted.ShouldContain("/data/record/title");
        fixture.Redacted.ShouldContain("/data/old_record/title");
        fixture.Redacted.ShouldNotContain("/data/record/note");
    }

    [Fact]
    public void No_original_non_key_value_survives_anywhere_in_the_fixture()
    {
        var text = DeliveryFixtures.Of(Delivery(), Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), _schema).Envelope.GetRawText();

        text.ShouldNotContain("Printer jam");
        text.ShouldNotContain("12.5");
        text.ShouldNotContain("legacy-system");
        text.ShouldNotContain("true", Case.Sensitive);
    }

    [Theory]
    [InlineData("orders.shipped", "an application event keeps only id")]
    [InlineData("entity.retired_tickets.updated", "an entity no longer declared keeps only id")]
    public void An_event_with_no_declared_entity_keeps_only_id(string type, string because)
    {
        var record = DeliveryFixtures.Of(Delivery(), Envelope(type, Row("Printer jam", 12.5m)), _schema)
            .Envelope.GetProperty("data").GetProperty("record");

        record.GetProperty("id").GetString().ShouldBe(_ticket.ToString(), because);
        record.GetProperty("status").GetString().ShouldBe("<redacted:status>", because);
        record.GetProperty("customer_id").GetString().ShouldBe("<redacted:customer_id>", because);
        record.GetProperty("tenant_id").GetString().ShouldBe("<redacted:tenant_id>", because);
    }

    [Fact]
    public void The_envelope_s_attributes_and_the_changed_names_stay()
    {
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), _schema);

        fixture.Envelope.GetProperty("type").GetString().ShouldBe("entity.tickets.updated");
        fixture.Envelope.GetProperty("subject").GetString().ShouldBe($"tickets/{_ticket}", "a data event's subject is the record's id");
        fixture.Envelope.GetProperty("partitionkey").GetString().ShouldBe($"tickets:{_ticket}");
        fixture.Envelope.GetProperty("tenantid").GetString().ShouldBe(_tenant.ToString());
        fixture.Envelope.GetProperty("authclaims").GetString().ShouldBe("""{"roles":["admin"]}""");
        fixture.Envelope.GetProperty("authid").GetString().ShouldBe(_customer.ToString());
        fixture.Envelope.GetProperty("causationid").GetString().ShouldBe(_ticket.ToString());
        fixture.Envelope.GetProperty("correlationid").GetString().ShouldBe("4bf92f3577b34da6a3ce929d0e0e4736");
        fixture.Envelope.GetProperty("traceparent").GetString().ShouldBe("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        fixture.Envelope.GetProperty("data").GetProperty("changed").EnumerateArray().Select(name => name.GetString()).ShouldContain("title");
        fixture.Subscriber.ShouldBe("send-confirmation");
        fixture.Attempt.ShouldBe(3);
    }

    [Fact]
    public void An_application_event_s_subject_and_key_value_are_redacted_and_its_trace_state_dropped()
    {
        var fixture = DeliveryFixtures.Of(Delivery(), Envelope("orders.shipped", Row("Printer jam", 12.5m), traceState: "vendor=customer-42"), _schema);

        fixture.Envelope.GetProperty("subject").GetString().ShouldBe("<redacted:subject>");
        fixture.Envelope.GetProperty("partitionkey").GetString().ShouldBe("custom.event:<redacted:partitionkey>", "the marker IAlvoEvents.PublishAsync writes stays");
        fixture.Envelope.TryGetProperty("tracestate", out _).ShouldBeFalse();
        fixture.Redacted.ShouldContain("/tracestate");
    }

    [Fact]
    public void An_attribute_this_build_does_not_know_is_redacted_whole()
    {
        var envelope = Edit(Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), node =>
        {
            node["vendorext"] = "customer-42";
            node["correlationid"] = new JsonObject { ["nested"] = "customer-42" };
        });

        var fixture = DeliveryFixtures.Of(Delivery(), envelope, _schema);

        fixture.Envelope.GetProperty("vendorext").GetString().ShouldBe("<redacted:vendorext>");
        fixture.Envelope.GetProperty("correlationid").GetString().ShouldBe("<redacted:correlationid>", "a known attribute that is not a scalar is not what this build wrote");
        fixture.Redacted.ShouldContain("/vendorext");
        fixture.Envelope.GetRawText().ShouldNotContain("customer-42");
    }

    [Fact]
    public void Claims_beyond_the_role_names_redact_the_whole_claims_attribute()
    {
        var envelope = Edit(Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), node =>
            node["authclaims"] = """{"roles":["admin"],"email":"jane@example.com"}""");

        var fixture = DeliveryFixtures.Of(Delivery(), envelope, _schema);

        fixture.Envelope.GetProperty("authclaims").GetString().ShouldBe("<redacted:authclaims>");
        fixture.Envelope.GetRawText().ShouldNotContain("jane@example.com");
    }

    [Fact]
    public void A_key_field_whose_value_is_not_a_uuid_or_an_enum_outside_its_values_is_redacted()
    {
        var envelope = Edit(Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), node =>
        {
            var record = node["data"]!["record"]!;
            record["customer_id"] = "jane@example.com";
            record["status"] = "jane@example.com";
            record["tenant_id"] = new JsonArray("jane@example.com");
            node["data"]!["extra"] = "jane@example.com";
            node["subject"] = "tickets/jane@example.com";
            node["partitionkey"] = "tickets:jane@example.com";
        });

        var fixture = DeliveryFixtures.Of(Delivery(), envelope, _schema);

        var record = fixture.Envelope.GetProperty("data").GetProperty("record");
        record.GetProperty("customer_id").GetString().ShouldBe("<redacted:customer_id>");
        record.GetProperty("status").GetString().ShouldBe("<redacted:status>");
        record.GetProperty("tenant_id").GetString().ShouldBe("<redacted:tenant_id>");
        fixture.Envelope.GetProperty("data").GetProperty("extra").GetString().ShouldBe("<redacted:extra>");
        fixture.Envelope.GetProperty("subject").GetString().ShouldBe("<redacted:subject>");
        fixture.Envelope.GetProperty("partitionkey").GetString().ShouldBe("tickets:<redacted:partitionkey>");
        fixture.Envelope.GetRawText().ShouldNotContain("jane@example.com");
    }

    [Theory]
    [InlineData("authid", "jane@example.com")]
    [InlineData("causationid", "jane@example.com")]
    [InlineData("correlationid", "jane@example.com")]
    [InlineData("correlationid", "4bf92f3577b34da6a3ce929d0e0e47")]
    [InlineData("traceparent", "00-jane@example.com-00f067aa0ba902b7-01")]
    [InlineData("traceparent", "ff-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")]
    [InlineData("tenantid", "jane@example.com")]
    [InlineData("id", "jane@example.com")]
    public void An_identifying_attribute_not_in_the_shape_this_build_writes_is_redacted(string attribute, string value)
    {
        var envelope = Edit(Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), node => node[attribute] = value);

        var fixture = DeliveryFixtures.Of(Delivery(), envelope, _schema);

        fixture.Envelope.GetProperty(attribute).GetString().ShouldBe($"<redacted:{attribute}>");
        fixture.Redacted.ShouldContain($"/{attribute}");
    }

    [Theory]
    [InlineData("orders.shipped", "orders.shipped:jane@example.com")]
    [InlineData("orders.shipped", "jane@example.com")]
    [InlineData("entity.tickets.updated", "deals:jane@example.com")]
    public void A_partition_key_in_a_namespace_this_build_does_not_write_is_redacted_whole(string type, string key)
    {
        var envelope = Edit(Envelope(type, Row("Printer jam", 12.5m)), node => node["partitionkey"] = key);

        var fixture = DeliveryFixtures.Of(Delivery(), envelope, _schema);

        fixture.Envelope.GetProperty("partitionkey").GetString().ShouldBe("<redacted:partitionkey>");
    }

    [Fact]
    public void A_changed_list_naming_a_field_the_declared_entity_lacks_is_redacted()
    {
        var envelope = Edit(Envelope("entity.tickets.updated", Row("Printer jam", 12.5m)), node =>
            node["data"]!["changed"] = new JsonArray("title", "jane@example.com"));

        var fixture = DeliveryFixtures.Of(Delivery(), envelope, _schema);

        fixture.Envelope.GetProperty("data").GetProperty("changed").GetString().ShouldBe("<redacted:changed>");
        fixture.Redacted.ShouldContain("/data/changed");
    }

    [Theory]
    [InlineData("not json jane@example.com")]
    [InlineData("""["jane@example.com"]""")]
    [InlineData("""{"id":"jane@example.com","id":"x"}""")]
    public void A_payload_that_is_not_an_envelope_is_refused_without_echoing_it(string payload)
    {
        var refusal = Should.Throw<JsonException>(() => DeliveryFixtures.Of(Delivery(), payload, _schema));

        refusal.Message.ShouldNotContain("jane");
        refusal.InnerException.ShouldBeNull();
    }

    [Fact]
    public void Random_payloads_leave_no_non_key_scalar() => DeliveryFixturesProperty.Holds(_tenant);

    [Fact]
    public void A_placeholder_names_its_field() => DeliveryFixtures.Placeholder("title").ShouldBe("<redacted:title>");

    private static string Edit(string envelope, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(envelope)!.AsObject();
        edit(node);

        return node.ToJsonString();
    }

    private static StoredDelivery Delivery() => new()
    {
        Id = Guid.Parse("0192f1a4-0000-7000-8000-000000000001"),
        EventId = Guid.Parse("0192f1a4-0000-7000-8000-000000000002"),
        Subscriber = "send-confirmation",
        PartitionKey = $"tickets:{_ticket}",
        Kind = "handler",
        Status = "dead",
        Attempts = 3,
        NotBefore = DateTimeOffset.UnixEpoch,
    };

    private static AlvoRecord Row(string title, decimal total) => new(new Dictionary<string, object?>
    {
        ["id"] = _ticket,
        ["tenant_id"] = _tenant,
        ["title"] = title,
        ["total"] = total,
        ["is_urgent"] = true,
        ["external_ref"] = "legacy-system",
        ["metadata"] = """{"source":"legacy-system"}""",
        ["status"] = "open",
        ["customer_id"] = _customer,
        ["note"] = null,
    });

    private static string Envelope(string type, AlvoRecord record, string? traceState = null) => AlvoEventJson.Write(new AlvoEvent
    {
        Id = Guid.Parse("0192f1a4-0000-7000-8000-000000000002"),
        Source = AlvoEvent.DefaultSource,
        Type = type,
        Time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Subject = $"tickets/{_ticket}",
        PartitionKey = type.StartsWith("entity.", StringComparison.Ordinal) ? $"tickets:{_ticket}" : $"custom.event:orders/{_ticket}",
        AuthType = AlvoEventAuthType.ApiKey,
        AuthId = _customer.ToString(),
        AuthClaims = """{"roles":["admin"]}""",
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        CausationId = _ticket.ToString(),
        TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        TenantId = _tenant.ToString(),
        TraceState = traceState,
        Data = new AlvoEventData { Record = record, OldRecord = record, Changed = ["title"] },
    });
}
