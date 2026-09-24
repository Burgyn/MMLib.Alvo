using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The field editor's values, and the declaration built from them in the frozen schema's shape.
/// </summary>
public class FieldFacetsTests
{
    [Fact]
    public void A_new_string_field_carries_its_type_and_max_length()
    {
        var facets = Built(new FieldFacets { Name = "title", Required = true });

        facets.ToJsonString().ShouldBe("""{"type":"string","required":true,"maxLength":120}""");
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("")]
    [InlineData("has space")]
    public void A_name_the_schema_does_not_admit_is_refused(string name)
        => Refusal(new FieldFacets { Name = name }).ShouldContain(DescriptorNames.Member);

    [Fact]
    public void A_name_another_field_already_has_is_refused()
        => Refusal(new FieldFacets { Name = "status" }, siblings: ["status"])
            .ShouldBe("This entity already declares a field called status.");

    [Fact]
    public void Keeping_the_edited_fields_own_name_is_not_a_collision()
    {
        var editor = FieldFacets.Prefill("status", """{"type":"string"}""");

        editor.Build("status", """{"type":"string"}""", ["status"], out var refusal).ShouldNotBeNull();
        refusal.ShouldBeNull();
    }

    [Fact]
    public void An_enum_with_no_values_is_refused()
        => Refusal(new FieldFacets { Name = "state", Type = FieldType.Enum, Values = " , " })
            .ShouldBe("An enum needs at least one value — the schema requires it.");

    [Fact]
    public void An_enums_values_are_trimmed_into_an_array()
        => Built(new FieldFacets { Name = "state", Type = FieldType.Enum, Values = "open, done ,," })["values"]!
            .ToJsonString().ShouldBe("""["open","done"]""");

    [Fact]
    public void A_ref_with_no_target_is_refused()
        => Refusal(new FieldFacets { Name = "bike", Type = FieldType.Ref })
            .ShouldBe("A ref needs the entity it points at — the schema requires it.");

    [Fact]
    public void A_ref_restricts_deletes_unless_it_already_says_otherwise()
    {
        var editor = new FieldFacets { Name = "bike", Type = FieldType.Ref, Target = "bikes" };

        editor.Build(null, null, [], out _)!["onDelete"]!.GetValue<string>().ShouldBe("restrict");
        editor.Build("bike", """{"type":"ref","entity":"bikes","onDelete":"cascade"}""", [], out _)!["onDelete"]!
            .GetValue<string>().ShouldBe("cascade");
    }

    [Theory]
    [InlineData(FieldType.Integer, "42", "42")]
    [InlineData(FieldType.Decimal, "21.60", "21.60")]
    [InlineData(FieldType.Boolean, "true", "true")]
    [InlineData(FieldType.Date, "2026-01-01", "\"2026-01-01\"")]
    public void A_default_is_written_as_a_literal_of_the_fields_type(FieldType type, string typed, string json)
        => Built(new FieldFacets { Name = "value", Type = type, Default = typed, Values = "a" })["default"]!
            .ToJsonString().ShouldBe(json);

    [Theory]
    [InlineData(FieldType.Integer)]
    [InlineData(FieldType.Decimal)]
    public void A_default_that_is_not_a_number_is_refused_for_a_numeric_field(FieldType type)
        => Refusal(new FieldFacets { Name = "value", Type = type, Default = "twelve" })
            .ShouldBe("'twelve' is not a number, and this field's default has to be one.");

    [Fact]
    public void An_empty_default_removes_the_declared_one()
    {
        var editor = FieldFacets.Prefill("status", """{"type":"string","default":"open"}""");
        editor.Default = string.Empty;

        editor.Build("status", """{"type":"string","default":"open"}""", [], out _)!
            .ContainsKey("default").ShouldBeFalse();
    }

    [Fact]
    public void A_computed_fields_default_is_left_as_declared_because_there_is_no_control_for_it()
    {
        const string declared = """{"type":"decimal","precision":10,"scale":2,"computed":"a + b","default":1}""";
        var editor = FieldFacets.Prefill("total", declared);

        editor.TakesADefault.ShouldBeFalse();
        editor.Build("total", declared, [], out _)!["default"]!.GetValue<decimal>().ShouldBe(1m);
    }

    [Fact]
    public void Retyping_a_field_drops_the_facets_of_its_old_type()
    {
        const string declared = """{"type":"string","maxLength":40,"format":"email","description":"kept"}""";
        var editor = FieldFacets.Prefill("contact", declared);
        editor.Type = FieldType.Integer;

        var facets = editor.Build("contact", declared, [], out _)!;

        facets.ContainsKey("maxLength").ShouldBeFalse();
        facets.ContainsKey("format").ShouldBeFalse();
        facets["description"]!.GetValue<string>().ShouldBe("kept");
    }

    [Fact]
    public void A_decimal_carries_its_precision_and_scale()
        => Built(new FieldFacets { Name = "rate", Type = FieldType.Decimal, Precision = 12, Scale = 4 })
            .ToJsonString().ShouldBe("""{"type":"decimal","precision":12,"scale":4}""");

    [Fact]
    public void Clearing_unique_and_indexed_removes_the_declared_ones()
    {
        const string declared = """{"type":"string","maxLength":40,"unique":true,"index":true}""";
        var editor = FieldFacets.Prefill("code", declared);
        editor.Unique = false;
        editor.Indexed = false;

        editor.Build("code", declared, [], out _)!.ToJsonString()
            .ShouldBe("""{"type":"string","maxLength":40}""");
    }

    [Fact]
    public void Retyping_away_from_a_ref_drops_its_target_and_on_delete()
    {
        const string declared = """{"type":"ref","entity":"bikes","onDelete":"cascade"}""";
        var editor = FieldFacets.Prefill("bike", declared);
        editor.Type = FieldType.Uuid;

        editor.Build("bike", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"uuid"}""");
    }

    [Fact]
    public void A_boolean_default_of_false_is_the_literal_false()
        => Built(new FieldFacets { Name = "active", Type = FieldType.Boolean, Default = "false" })
            .ToJsonString().ShouldBe("""{"type":"boolean","default":false}""");

    [Fact]
    public void Prefill_reads_every_facet_the_editor_draws()
    {
        var editor = FieldFacets.Prefill(
            "state", """{"type":"enum","values":["open","done"],"required":true,"index":true,"default":"open"}""");

        editor.Type.ShouldBe(FieldType.Enum);
        editor.Values.ShouldBe("open, done");
        editor.Required.ShouldBeTrue();
        editor.Indexed.ShouldBeTrue();
        editor.Default.ShouldBe("open");
        editor.DefaultPlaceholder.ShouldBe("e.g. open");
    }

    [Fact]
    public void A_declaration_that_is_not_json_is_read_as_an_empty_one()
        => FieldFacets.Parse("{not json").Count.ShouldBe(0);

    [Theory]
    [InlineData(FieldType.Ref, false)]
    [InlineData(FieldType.Json, false)]
    [InlineData(FieldType.Text, true)]
    public void Only_a_type_that_takes_a_literal_takes_a_default(FieldType type, bool takes)
        => new FieldFacets { Type = type }.TakesADefault.ShouldBe(takes);

    private static JsonObject Built(FieldFacets editor)
    {
        var facets = editor.Build(null, null, [], out var refusal);
        refusal.ShouldBeNull();
        return facets.ShouldNotBeNull();
    }

    private static string Refusal(FieldFacets editor, IReadOnlyList<string>? siblings = null)
    {
        editor.Build(null, null, siblings ?? [], out var refusal).ShouldBeNull();
        return refusal.ShouldNotBeNull();
    }
}
