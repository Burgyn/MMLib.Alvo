using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A rollup and a computed field in the editor: composed only as the apply accepts them (<c>RollupResolver</c>,
/// <c>ComputedColumnSql</c>), opened in their own kind, never as a plain integer (docs/todo-admin.md §7).
/// </summary>
public class FieldFacetsMaintainedTests
{
    private static readonly RollupSource _orders = new(
        "orders", ["customer_id"],
        [new("priority", FieldType.Integer, null, null), new("total", FieldType.Decimal, 10, 2)], null);

    /// <summary>How the working copy writes JSON (<c>WorkingCopy._pretty</c>'s encoder): a bare <c>ToJsonString()</c> escapes <c>+</c>.</summary>
    private static readonly JsonSerializerOptions _relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly RollupSource _follows = new("follows", ["follower", "followee"], [], null);

    [Fact]
    public void A_count_rollup_is_an_integer_and_names_no_field()
        => Built(Rollup("orders", "count")).ToJsonString()
            .ShouldBe("""{"type":"integer","rollup":{"from":"orders","op":"count"}}""");

    [Fact]
    public void A_sum_takes_the_child_fields_type_precision_and_scale()
    {
        var editor = Rollup("orders", "sum");
        editor.RollupField = "total";

        Built(editor).ToJsonString().ShouldBe(
            """{"type":"decimal","precision":10,"scale":2,"rollup":{"from":"orders","op":"sum","field":"total"}}""");
    }

    [Fact]
    public void An_avg_is_offered_over_decimal_fields_only()
        => Rollup("orders", "avg").Aggregatable.Select(field => field.Name).ShouldBe(["total"]);

    [Fact]
    public void A_sum_with_no_field_is_refused_as_the_apply_refuses_it()
        => Refusal(Rollup("orders", "sum")).ShouldContain("pick one");

    [Fact]
    public void A_child_with_two_refs_here_needs_the_one_to_follow()
    {
        var editor = Rollup("follows", "count");
        Refusal(editor).ShouldContain("follower, followee");

        editor.RollupVia = "followee";
        Built(editor)["rollup"]!["via"]!.GetValue<string>().ShouldBe("followee");
    }

    [Fact]
    public void A_source_the_apply_refuses_is_refused_with_its_reason()
    {
        var editor = new FieldFacets
        {
            Name = "drafts_count",
            Kind = FieldKind.Rollup,
            RollupFrom = "drafts",
            Sources = [new("drafts", ["customer_id"], [], "drafts is a dynamic entity …")],
        };

        Refusal(editor).ShouldBe("drafts is a dynamic entity …");
    }

    [Fact]
    public void An_existing_rollup_opens_as_a_rollup_and_is_written_back_unchanged()
    {
        const string declared = """{"type":"integer","description":"How many bikes","rollup":{"from":"bikes","op":"count"}}""";
        var editor = FieldFacets.Prefill("bikes_count", declared);
        editor.Sources = [new("bikes", ["customer_id"], [], null)];

        editor.Kind.ShouldBe(FieldKind.Rollup);
        editor.RollupFrom.ShouldBe("bikes");
        editor.RequiredOffered.ShouldBeFalse();
        editor.UniqueOffered.ShouldBeFalse();
        editor.Build("bikes_count", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void A_declared_filter_is_refused_until_it_is_removed()
    {
        const string declared = """{"type":"integer","rollup":{"from":"orders","op":"count","where":"priority > 1"}}""";
        var editor = FieldFacets.Prefill("urgent", declared);
        editor.Sources = [_orders];

        editor.DeclaresRollupFilter.ShouldBeTrue();
        editor.Build("urgent", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("rollup.where");

        editor.RemoveRollupFilter = true;
        editor.Build("urgent", declared, [], out _)!["rollup"]!.AsObject().ContainsKey("where").ShouldBeFalse();
    }

    [Fact]
    public void Required_and_unique_on_a_rollup_are_carried_and_said_not_offered()
    {
        const string declared = """{"type":"integer","required":true,"unique":true,"rollup":{"from":"orders","op":"count"}}""";
        var editor = FieldFacets.Prefill("orders_count", declared);
        editor.Sources = [_orders];

        var facets = editor.Build("orders_count", declared, [], out _)!;
        facets["required"]!.GetValue<bool>().ShouldBeTrue();
        editor.Notes().Where(note => note.Fate == FacetFate.Kept).Select(note => note.Facet)
            .ShouldBe(["required", "unique"], ignoreOrder: true);
    }

    [Fact]
    public void Switching_a_supplied_string_to_a_rollup_drops_its_default_and_string_facets()
    {
        const string declared = """{"type":"string","maxLength":40,"default":"none"}""";
        var editor = FieldFacets.Prefill("orders_count", declared);
        editor.Sources = [_orders];
        editor.Kind = FieldKind.Rollup;
        editor.RollupFrom = "orders";

        editor.Build("orders_count", declared, [], out _)!.ToJsonString()
            .ShouldBe("""{"type":"integer","rollup":{"from":"orders","op":"count"}}""");
        editor.Notes().ShouldContain(note => note.Facet == "default" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void A_field_declaring_both_rollup_and_computed_opens_as_a_rollup_and_loses_the_computed()
    {
        const string declared = """{"type":"integer","computed":"a + b","rollup":{"from":"orders","op":"count"}}""";
        var editor = FieldFacets.Prefill("both", declared);
        editor.Sources = [_orders];

        editor.Build("both", declared, [], out _)!.ContainsKey("computed").ShouldBeFalse();
    }

    [Fact]
    public void A_computed_field_carries_its_expression_and_chosen_type()
        => Built(new FieldFacets { Name = "twice", Kind = FieldKind.Computed, Type = FieldType.Integer, Computed = " priority + priority " })
            .ToJsonString(_relaxed).ShouldBe("""{"type":"integer","computed":"priority + priority"}""");

    [Fact]
    public void A_computed_field_with_no_expression_is_refused()
        => Refusal(new FieldFacets { Name = "twice", Kind = FieldKind.Computed, Type = FieldType.Integer })
            .ShouldContain("needs its expression");

    [Fact]
    public void A_computed_field_of_a_type_it_is_not_offered_as_is_refused()
        => Refusal(new FieldFacets { Name = "twice", Kind = FieldKind.Computed, Type = FieldType.Json, Computed = "a" })
            .ShouldContain("generated column");

    [Fact]
    public void An_existing_computed_field_opens_as_computed_and_is_written_back_unchanged()
    {
        const string declared = """{"type":"decimal","description":"Hours times rate","precision":10,"scale":2,"computed":"labour_hours * labour_rate"}""";
        var editor = FieldFacets.Prefill("labour_total", declared);

        editor.Kind.ShouldBe(FieldKind.Computed);
        editor.Computed.ShouldBe("labour_hours * labour_rate");
        editor.Build("labour_total", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void Switching_a_computed_field_back_to_supplied_drops_the_expression()
    {
        const string declared = """{"type":"integer","computed":"a + b"}""";
        var editor = FieldFacets.Prefill("sum", declared);
        editor.Kind = FieldKind.Supplied;

        editor.Build("sum", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"integer"}""");
    }

    private static FieldFacets Rollup(string from, string op) => new()
    {
        Name = "value",
        Kind = FieldKind.Rollup,
        RollupFrom = from,
        RollupOp = op,
        Sources = [_orders, _follows],
    };

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
