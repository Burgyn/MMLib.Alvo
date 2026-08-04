using MMLib.Alvo.Events;

using System.Text.Json;

using static MMLib.Alvo.Abstractions.Tests.Events.SampleEvents;

namespace MMLib.Alvo.Abstractions.Tests.Events;

/// <summary>
/// The wire form, against CloudEvents v1.0.2's three deciding rules: the names, the seven-type system, and
/// how extensions are serialized. The SDK-backed oracle for the same rules lives in
/// <c>MMLib.Alvo.Tests.Events.CloudEventsConformanceTests</c> — this project may take no package reference
/// (it tests the assembly that may take no dependency), so the two halves are deliberately split.
/// </summary>
public class AlvoEventJsonTests
{
    [Fact]
    public void Extensions_are_flat_top_level_members_never_a_nested_object()
    {
        using var document = JsonDocument.Parse(AlvoEventJson.Write(Sample()));
        var root = document.RootElement;

        root.TryGetProperty("extensions", out _).ShouldBeFalse(
            "CloudEvents v1.0.2:439-440 serializes extensions like standard attributes; a nested "
            + "wrapper is non-conformant");
        foreach (var extension in AlvoEventAttributes.Extensions)
        {
            root.TryGetProperty(extension, out _).ShouldBeTrue(extension);
        }
    }

    /// <summary>
    /// The seven-type system has no map or array (spec v1.0.2:179-217), so these three cannot be context
    /// attributes at all — which is the single most-repeated defect in the base design's envelope.
    /// </summary>
    [Theory]
    [InlineData("record")]
    [InlineData("old_record")]
    [InlineData("changed")]
    public void The_row_images_and_the_changed_list_live_inside_data(string member)
    {
        using var document = JsonDocument.Parse(AlvoEventJson.Write(Sample()));

        document.RootElement.TryGetProperty(member, out _).ShouldBeFalse();
        document.RootElement.GetProperty(AlvoEventAttributes.Data)
            .TryGetProperty(member, out _).ShouldBeTrue();
    }

    [Fact]
    public void The_wire_specversion_is_1_0_not_1_0_2()
    {
        using var document = JsonDocument.Parse(AlvoEventJson.Write(Sample()));

        document.RootElement.GetProperty(AlvoEventAttributes.SpecVersion).GetString().ShouldBe("1.0");
    }

    [Fact]
    public void An_envelope_round_trips_through_write_and_read()
        => AlvoEventJson.Read(AlvoEventJson.Write(Sample())).ShouldBe(Sample());

    /// <summary>
    /// An absent optional attribute is absent, not null: CloudEvents forbids an attribute present with no
    /// value, and a consumer switching on presence must not see <c>causationid: null</c>.
    /// </summary>
    [Fact]
    public void An_absent_optional_attribute_is_omitted_rather_than_written_as_null()
    {
        using var document = JsonDocument.Parse(
            AlvoEventJson.Write(Sample() with { CausationId = null, AuthId = null }));

        document.RootElement.TryGetProperty(AlvoEventAttributes.CausationId, out _).ShouldBeFalse();
        document.RootElement.TryGetProperty(AlvoEventAttributes.AuthId, out _).ShouldBeFalse();
    }

    [Fact]
    public void An_absent_row_image_is_omitted_from_data_rather_than_written_as_null()
    {
        var created = Sample() with { Data = Sample().Data with { OldRecord = null } };

        using var document = JsonDocument.Parse(AlvoEventJson.Write(created));

        document.RootElement.GetProperty(AlvoEventAttributes.Data)
            .TryGetProperty("old_record", out _).ShouldBeFalse();
    }

    [Fact]
    public void Every_clr_type_a_record_can_hold_has_exactly_one_json_rendering()
    {
        using var document = JsonDocument.Parse(AlvoEventJson.Write(SampleWith(EveryValueType())));
        var record = document.RootElement.GetProperty(AlvoEventAttributes.Data).GetProperty("record");

        record.GetProperty("text").GetString().ShouldBe("vw");
        record.GetProperty("uuid").GetString().ShouldBe(FixedRowId.ToString());
        record.GetProperty("flag").GetBoolean().ShouldBeTrue();
        record.GetProperty("count").GetInt64().ShouldBe(42);
        record.GetProperty("price").GetDecimal().ShouldBe(19.99m);
        record.GetProperty("ratio").GetDouble().ShouldBe(0.5);
        record.GetProperty("moment").GetString().ShouldBe("2026-08-03T09:30:00.0000000+00:00");
        record.GetProperty("day").GetString().ShouldBe("2026-08-03");
        record.GetProperty("nothing").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// A value the writer does not recognise is refused rather than stringified through
    /// <see cref="object.ToString"/>: a wire format that guesses delivers a body its author never declared,
    /// and the failure would surface in the consumer rather than here.
    /// </summary>
    [Fact]
    public void A_value_the_writer_does_not_know_is_refused_and_the_refusal_names_the_field()
    {
        var refusal = Should.Throw<NotSupportedException>(
            () => AlvoEventJson.Write(SampleWith(Record(("odd", new Uri("https://example.test"))))));

        refusal.Message.ShouldContain("odd");
        refusal.Message.ShouldContain(nameof(Uri));
    }

    /// <summary>
    /// A decision on the record rather than an oversight: JSON carries no CLR type, so what
    /// <see cref="AlvoEventJson.Read"/> returns is JSON's view of a field, not the row's. The read side's one
    /// consumer is the dispatcher, which evaluates CEL conditions and renders templates over the textual
    /// view anyway; the authoritative typed record lives on the write path, where the schema is in scope.
    /// </summary>
    [Fact]
    public void A_uuid_field_reads_back_as_its_text_because_json_carries_no_clr_type()
    {
        var written = AlvoEventJson.Write(SampleWith(Record(("uuid", FixedRowId))));

        AlvoEventJson.Read(written).Data.Record!["uuid"].ShouldBe(FixedRowId.ToString());
    }

    [Fact]
    public void A_number_reads_back_as_the_narrowest_type_that_holds_it()
    {
        var written = AlvoEventJson.Write(SampleWith(Record(("count", 42L), ("price", 19.99m))));

        var record = AlvoEventJson.Read(written).Data.Record!;
        record["count"].ShouldBe(42L);
        record["price"].ShouldBe(19.99m);
    }

    [Fact]
    public void Reading_an_envelope_missing_a_required_attribute_names_the_attribute()
    {
        var withoutType = AlvoEventJson.Write(Sample())
            .Replace($"\"{AlvoEventAttributes.Type}\"", "\"nottype\"", StringComparison.Ordinal);

        Should.Throw<JsonException>(() => AlvoEventJson.Read(withoutType))
            .Message.ShouldContain(AlvoEventAttributes.Type);
    }

    /// <summary>
    /// The guard holds on the way in as well as on the way out: an envelope another producer wrote with a
    /// local offset is refused rather than silently ordered against a different instant.
    /// </summary>
    [Fact]
    public void Reading_an_envelope_whose_time_carries_an_offset_is_refused_by_the_envelopes_own_guard()
        => Should.Throw<ArgumentException>(
                () => AlvoEventJson.Read(EnvelopeWithTime("2026-08-03T09:30:00.0000000+02:00")))
            .Message.ShouldContain("UTC");

    [Fact]
    public void Reading_an_envelope_from_another_spec_version_is_refused()
        => Should.Throw<JsonException>(
                () => AlvoEventJson.Read(EnvelopeWithTime("2026-08-03T09:30:00.0000000+00:00", specVersion: "0.3")))
            .Message.ShouldContain(AlvoEventAttributes.SpecVersion);

    /// <summary>
    /// The writer keeps <see cref="System.Text.Json"/>'s default HTML-safe encoder, so a value that could
    /// close an HTML context reaches a webhook or a dashboard escaped. Secure-by-default costs a handful of
    /// bytes per event — visible in the pinned snapshot as <c>+</c> in the timestamp — and never costs
    /// correctness, which is what the round-trip half of this fact holds.
    /// </summary>
    [Fact]
    public void A_value_that_could_close_an_html_context_is_escaped_and_still_round_trips()
    {
        const string Payload = "<script>alert('x')</script>";

        var written = AlvoEventJson.Write(SampleWith(Record(("note", Payload))));

        written.ShouldNotContain("<script>");
        AlvoEventJson.Read(written).Data.Record!["note"].ShouldBe(Payload);
    }

    private static string EnvelopeWithTime(string time, string specVersion = "1.0") =>
        $$"""
        {"specversion":"{{specVersion}}","id":"019fc77e-be7b-72e8-b7fd-ffd6f6306e3e",
         "source":"/alvo","type":"entity.vehicles.updated","time":"{{time}}",
         "subject":"vehicles/one","datacontenttype":"application/json",
         "partitionkey":"vehicles:one","payloadversion":1,"chaindepth":0,
         "authtype":"apikey","correlationid":"trace","data":{"changed":[]} }
        """;

    private static MMLib.Alvo.Data.AlvoRecord EveryValueType() => Record(
        ("text", "vw"),
        ("uuid", FixedRowId),
        ("flag", true),
        ("count", 42L),
        ("price", 19.99m),
        ("ratio", 0.5d),
        ("moment", FixedTime),
        ("day", new DateOnly(2026, 8, 3)),
        ("nothing", null));
}
