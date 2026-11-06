using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A <c>sequence</c> in the field editor: a third maintained kind beside rollup and computed, composed only as the apply
/// accepts it (<c>SequenceFieldCheck</c>) and written back exactly as declared.
/// </summary>
public class FieldFacetsSequenceTests
{
    private static readonly DateTimeOffset _clock = new(2026, 11, 6, 9, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("""{"type":"integer","sequence":{}}""")]
    [InlineData("""{"type":"integer","description":"The order number","sequence":{"per":"room","period":"year","start":100}}""")]
    [InlineData("""{"type":"string","sequence":{"per":"room","period":"month","format":"B-{yyyy}-{n:3}"}}""")]
    public void A_sequence_round_trips_through_the_maintained_facets(string declared)
    {
        var editor = FieldFacets.Prefill("number", declared);

        editor.Kind.ShouldBe(FieldKind.Sequence);
        editor.MaintainedElsewhere.ShouldBeTrue();
        editor.Build("number", declared, [], out var refusal)!.ToJsonString(Relaxed.Options).ShouldBe(declared);
        refusal.ShouldBeNull();
    }

    [Fact]
    public void A_new_integer_sequence_writes_only_what_was_chosen()
        => Built(new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.Integer, SequencePer = "room", SequencePeriod = "day" })
            .ToJsonString().ShouldBe("""{"type":"integer","sequence":{"per":"room","period":"day"}}""");

    [Fact]
    public void Choosing_sequence_on_a_string_field_requires_a_format()
    {
        var editor = new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.String };

        Refusal(editor).ShouldContain("format");
        editor.SequenceFormat = "B-{n:4}";
        Built(editor).ToJsonString().ShouldBe("""{"type":"string","sequence":{"format":"B-{n:4}"}}""");
    }

    [Fact]
    public void An_integer_sequence_writes_no_format()
        => Built(new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.Integer, SequenceFormat = "B-{n}" })
            .ToJsonString().ShouldBe("""{"type":"integer","sequence":{}}""");

    [Fact]
    public void A_sequence_is_offered_as_an_integer_or_a_string_only()
    {
        FieldFacets.SequenceTypes.ShouldBe([FieldType.Integer, FieldType.String]);
        Refusal(new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.Decimal })
            .ShouldContain("integer or a string");
    }

    [Theory]
    [InlineData("B-{yyyy}-{n:3}", null, "B-2026-001")]
    [InlineData("{yy}{MM}{dd}/{n}", null, "261106/1")]
    [InlineData("INV-{n:5}", "41", "INV-00041")]
    public void The_format_is_checked_live_and_previews_the_next_number(string format, string? start, string preview)
    {
        var editor = new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.String, SequenceFormat = format, SequenceStart = start ?? string.Empty };

        editor.SequenceFormatProblem.ShouldBeNull();
        editor.SequencePreview(_clock).ShouldBe(preview);
    }

    [Theory]
    [InlineData("B-{n}-{n}")]
    [InlineData("B-{yyyy}")]
    [InlineData("B-{n:19}")]
    [InlineData("B-{n}{x}")]
    public void A_malformed_format_is_said_at_the_box_and_previews_nothing(string format)
    {
        var editor = new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.String, SequenceFormat = format };

        editor.SequenceFormatProblem.ShouldNotBeNull().ShouldContain("{n}");
        editor.SequencePreview(_clock).ShouldBeNull();
        Refusal(editor).ShouldBe(editor.SequenceFormatProblem);
    }

    [Fact]
    public void An_integer_sequence_previews_its_start()
        => new FieldFacets { Kind = FieldKind.Sequence, Type = FieldType.Integer, SequenceStart = "1000" }
            .SequencePreview(_clock).ShouldBe("1000");

    [Theory]
    [InlineData("-1")]
    [InlineData("one")]
    public void A_start_the_schema_refuses_is_refused(string start)
        => Refusal(new FieldFacets { Name = "number", Kind = FieldKind.Sequence, Type = FieldType.Integer, SequenceStart = start })
            .ShouldContain("start");

    [Fact]
    public void Editing_a_sequenced_field_s_description_keeps_its_sequence()
    {
        const string declared = """{"type":"string","description":"old","sequence":{"per":"room","format":"B-{n:3}"}}""";
        var editor = FieldFacets.Prefill("number", declared);
        var facets = FieldFacets.Parse(declared);
        facets["description"] = "new";

        var built = editor.Build("number", facets.ToJsonString(), [], out _)!;

        built.ToJsonString(Relaxed.Options).ShouldBe("""{"type":"string","description":"new","sequence":{"per":"room","format":"B-{n:3}"}}""");
    }

    [Fact]
    public void A_sequence_is_never_required_unique_or_defaulted()
    {
        var editor = new FieldFacets { Kind = FieldKind.Sequence, Type = FieldType.Integer };

        editor.RequiredOffered.ShouldBeFalse();
        editor.UniqueOffered.ShouldBeFalse();
        editor.TakesADefault.ShouldBeFalse();
    }

    [Fact]
    public void A_declared_max_length_is_removed_and_said()
    {
        const string declared = """{"type":"string","maxLength":20,"sequence":{"format":"{n}"}}""";
        var editor = FieldFacets.Prefill("number", declared);

        editor.Build("number", declared, [], out _)!.ContainsKey("maxLength").ShouldBeFalse();
        editor.Notes().ShouldContain(note => note.Facet == "maxLength" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void Switching_a_sequence_back_to_supplied_drops_it_and_says_so()
    {
        const string declared = """{"type":"integer","sequence":{"start":5}}""";
        var editor = FieldFacets.Prefill("number", declared);
        editor.Kind = FieldKind.Supplied;

        editor.Build("number", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"integer"}""");
        editor.Notes().ShouldContain(note => note.Facet == "sequence" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void Per_is_offered_over_required_countable_fields_this_entity_writes()
    {
        IReadOnlyList<KeyValuePair<string, string>> fields =
        [
            new("room", """{"type":"ref","entity":"rooms","required":true}"""),
            new("status", """{"type":"enum","values":["a"],"required":true}"""),
            new("note", """{"type":"string"}"""),
            new("price", """{"type":"decimal","precision":10,"scale":2,"required":true}"""),
            new("total", """{"type":"integer","required":true,"computed":"1 + 1"}"""),
            new("other_number", """{"type":"integer","required":true,"sequence":{}}"""),
            new("number", """{"type":"integer","required":true}"""),
        ];

        FieldFacets.SequencePerOffered(fields, "number").ShouldBe(["room", "status"]);
    }

    [Fact]
    public void A_declared_per_the_editor_would_not_offer_is_still_offered()
    {
        var editor = FieldFacets.Prefill("number", """{"type":"integer","sequence":{"per":"legacy"}}""");
        editor.PerOffered = ["room"];

        editor.PerChoices.ShouldBe(["", "room", "legacy"]);
    }

    private static JsonObject Built(FieldFacets editor)
    {
        var facets = editor.Build(null, null, [], out var refusal);
        refusal.ShouldBeNull();
        return facets.ShouldNotBeNull();
    }

    private static string Refusal(FieldFacets editor)
    {
        editor.Build(null, null, [], out var refusal).ShouldBeNull();
        return refusal.ShouldNotBeNull();
    }
}
