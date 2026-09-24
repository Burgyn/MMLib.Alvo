using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// An existing maintained field the core accepts is saved as declared: the editor's conservative offer applies to
/// new choices only (<c>RollupResolver.EnsureAggregatedFieldIsResolvable</c> checks existence, not type).
/// </summary>
public class FieldFacetsMaintainedFixTests
{
    private static readonly RollupChildField[] _visitFields =
    [
        new("customer_id", FieldType.Ref, null, null),
        new("minutes", FieldType.Integer, null, null),
        new("seen_at", FieldType.DateTime, null, null),
    ];

    private static readonly RollupSource _visits = new(
        "visits", ["customer_id"], [new("minutes", FieldType.Integer, null, null)], null)
    { Fields = _visitFields };

    [Theory]
    [InlineData("""{"type":"decimal","precision":10,"scale":2,"rollup":{"from":"visits","op":"avg","field":"minutes"}}""")]
    [InlineData("""{"type":"datetime","rollup":{"from":"visits","op":"max","field":"seen_at"}}""")]
    public void An_existing_rollup_the_core_accepts_saves_unchanged(string declared)
        => Saved(declared).ShouldBe(declared);

    [Fact]
    public void A_decimal_sum_over_an_integer_child_keeps_its_type_and_facets()
        => Saved("""{"type":"decimal","precision":12,"scale":2,"rollup":{"from":"visits","op":"sum","field":"minutes"}}""")
            .ShouldBe("""{"type":"decimal","precision":12,"scale":2,"rollup":{"from":"visits","op":"sum","field":"minutes"}}""");

    [Fact]
    public void A_count_declared_decimal_keeps_its_type()
        => Saved("""{"type":"decimal","precision":10,"scale":0,"rollup":{"from":"visits","op":"count"}}""")
            .ShouldBe("""{"type":"decimal","precision":10,"scale":0,"rollup":{"from":"visits","op":"count"}}""");

    [Fact]
    public void A_declared_type_that_cannot_hold_the_aggregate_is_derived_and_said()
    {
        const string declared = """{"type":"string","maxLength":5,"rollup":{"from":"visits","op":"count"}}""";
        var editor = Opened(declared);

        editor.Build("n", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"integer","rollup":{"from":"visits","op":"count"}}""");
        editor.Type.ShouldBe(FieldType.String);
        editor.Notes().ShouldContain(note => note.Facet == "type" && note.Value == "\"integer\"");
    }

    [Fact]
    public void A_new_choice_is_still_offered_conservatively()
    {
        var editor = Opened("""{"type":"datetime","rollup":{"from":"visits","op":"max","field":"seen_at"}}""");
        editor.RollupOp = "min";

        editor.Aggregatable.Select(child => child.Name).ShouldBe(["minutes"]);
    }

    [Fact]
    public void A_computed_declared_beside_a_rollup_is_removed_and_said()
        => Opened("""{"type":"integer","computed":"a + b","rollup":{"from":"visits","op":"count"}}""").Notes()
            .ShouldContain(note => note.Facet == "computed" && note.Fate == FacetFate.Removed);

    [Fact]
    public void A_via_that_is_not_a_ref_here_is_removed_and_said()
    {
        const string declared = """{"type":"integer","rollup":{"from":"visits","op":"count","via":"minutes"}}""";
        var editor = Opened(declared);

        editor.Build("n", declared, [], out _)!["rollup"]!.AsObject().ContainsKey("via").ShouldBeFalse();
        editor.Notes().ShouldContain(note => note.Facet == "rollup.via" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void A_field_on_a_count_is_removed_and_said()
    {
        const string declared = """{"type":"integer","rollup":{"from":"visits","op":"count","field":"minutes"}}""";
        var editor = Opened(declared);

        editor.Build("n", declared, [], out _)!["rollup"]!.AsObject().ContainsKey("field").ShouldBeFalse();
        editor.Notes().ShouldContain(note => note.Facet == "rollup.field" && note.Fate == FacetFate.Removed);
    }

    /// <summary>The crm shape: a single-ref child whose rollup names its one ref anyway.</summary>
    [Fact]
    public void A_valid_via_on_a_single_ref_child_is_kept()
        => Saved("""{"type":"integer","rollup":{"from":"visits","op":"count","via":"customer_id"}}""")
            .ShouldBe("""{"type":"integer","rollup":{"from":"visits","op":"count","via":"customer_id"}}""");

    [Fact]
    public void A_from_that_is_not_among_the_sources_is_refused()
    {
        const string declared = """{"type":"integer","rollup":{"from":"ghosts","op":"count"}}""";
        Opened(declared).Build("n", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("pick one of the entities that point here");
    }

    [Fact]
    public void An_existing_computed_field_of_a_type_not_offered_to_new_ones_saves_unchanged()
    {
        const string declared = """{"type":"enum","values":["low","high"],"computed":"priority > 1 ? 'high' : 'low'"}""";
        var editor = FieldFacets.Prefill("band", declared);

        editor.ComputedTypesOffered.ShouldContain(FieldType.Enum);
        editor.Build("band", declared, [], out _)!.ToJsonString(Relaxed.Options).ShouldBe(declared);
    }

    [Fact]
    public void A_too_long_expression_is_refused_with_the_limit()
    {
        var editor = new FieldFacets { Name = "x", Kind = FieldKind.Computed, Type = FieldType.Integer, Computed = new string('a', 2001) };
        editor.Build(null, null, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("2000");
    }

    private static FieldFacets Opened(string declared)
    {
        var editor = FieldFacets.Prefill("n", declared);
        editor.Sources = [_visits];
        return editor;
    }

    private static string Saved(string declared)
    {
        var facets = Opened(declared).Build("n", declared, [], out var refusal);
        refusal.ShouldBeNull();
        return facets.ShouldNotBeNull().ToJsonString(Relaxed.Options);
    }
}
