using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The editor writes what the operator chose and nothing else (docs/todo-admin.md §8d items 15 and 17): a facet
/// it does not draw is kept and shown, or removed and said — never rewritten from a stale prefill.
/// </summary>
public class FieldFacetsWriteRulesTests
{
    [Fact]
    public void Editing_an_unbounded_string_writes_it_back_unbounded()
    {
        const string declared = """{"type":"string","required":true}""";
        var editor = FieldFacets.Prefill("title", declared);

        editor.MaxLength.ShouldBeNull();
        editor.Build("title", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void Clearing_the_max_length_removes_it()
    {
        const string declared = """{"type":"string","maxLength":40}""";
        var editor = FieldFacets.Prefill("code", declared);
        editor.MaxLength = null;

        editor.Build("code", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"string"}""");
    }

    [Fact]
    public void A_max_length_below_the_schemas_minimum_is_refused()
        => Refusal(new FieldFacets { Name = "code", MaxLength = 0 }).ShouldContain("at least 1");

    /// <summary>An edit that changes nothing is not a staged change: every key stays where it was.</summary>
    [Fact]
    public void An_edit_that_changes_nothing_writes_the_declaration_back_unchanged()
    {
        const string declared = """{"type":"string","description":"Where to send correspondence","maxLength":160,"format":"email"}""";

        FieldFacets.Prefill("email", declared).Build("email", declared, [], out _)!.ToJsonString().ShouldBe(declared);
    }

    [Fact]
    public void A_declared_unique_is_offered_on_a_type_the_checkbox_is_not_drawn_for_and_can_be_cleared()
    {
        const string declared = """{"type":"ref","entity":"customers","onDelete":"restrict","unique":true}""";
        var editor = FieldFacets.Prefill("customer_id", declared);

        editor.UniqueOffered.ShouldBeTrue();
        editor.Unique = false;
        editor.Build("customer_id", declared, [], out _)!.ContainsKey("unique").ShouldBeFalse();
    }

    [Fact]
    public void A_unique_ticked_before_a_retype_to_boolean_is_not_written_where_nobody_can_see_it()
    {
        var editor = new FieldFacets { Name = "active", Unique = true, Type = FieldType.Boolean };

        editor.UniqueOffered.ShouldBeFalse();
        Built(editor).ContainsKey("unique").ShouldBeFalse();
    }

    [Theory]
    [InlineData(FieldType.Ref)]
    [InlineData(FieldType.Json)]
    public void A_default_does_not_follow_a_retype_into_a_type_that_takes_none(FieldType type)
    {
        const string declared = """{"type":"string","maxLength":40,"default":"open"}""";
        var editor = FieldFacets.Prefill("state", declared);
        editor.Type = type;
        editor.Target = "customers";

        editor.Build("state", declared, [], out _)!.ContainsKey("default").ShouldBeFalse();
        editor.Notes().ShouldContain(note => note.Facet == "default" && note.Fate == FacetFate.Removed);
    }

    [Fact]
    public void A_json_fields_own_default_is_kept_and_said()
    {
        const string declared = """{"type":"json","default":{"a":1}}""";
        var editor = FieldFacets.Prefill("meta", declared);

        editor.Build("meta", declared, [], out _)!["default"]!.ToJsonString().ShouldBe("""{"a":1}""");
        editor.Notes().Single(note => note.Facet == "default").Fate.ShouldBe(FacetFate.Kept);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("TRUE")]
    public void A_boolean_default_that_is_not_true_or_false_is_refused_rather_than_saved_as_false(string typed)
        => Refusal(new FieldFacets { Name = "active", Type = FieldType.Boolean, Default = typed })
            .ShouldBe($"'{typed}' is not true or false, and this field's default has to be one.");

    [Theory]
    [InlineData(FieldType.Uuid, "not-a-uuid", "is not a uuid")]
    [InlineData(FieldType.Date, "yesterday", "is not a date")]
    public void A_default_the_type_cannot_parse_is_refused_as_the_apply_refuses_it(FieldType type, string typed, string says)
        => Refusal(new FieldFacets { Name = "value", Type = type, Default = typed }).ShouldContain(says);

    [Fact]
    public void An_enum_default_outside_its_values_is_refused()
        => Refusal(new FieldFacets { Name = "state", Type = FieldType.Enum, Values = "open, done", Default = "closed" })
            .ShouldContain("is not one of the values above");

    [Fact]
    public void A_string_default_longer_than_its_max_length_is_refused()
        => Refusal(new FieldFacets { Name = "code", MaxLength = 3, Default = "ABCD" })
            .ShouldBe("'ABCD' is 4 characters and the max length is 3.");

    [Fact]
    public void A_cel_default_is_not_put_in_the_box_and_is_not_rewritten_as_a_string()
    {
        const string declared = """{"type":"string","default":{"$cel":"now()"}}""";
        var editor = FieldFacets.Prefill("stamp", declared);

        editor.Default.ShouldBeEmpty();
        editor.DrawsDefault.ShouldBeFalse();
        editor.Build("stamp", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("$cel");
    }

    [Fact]
    public void A_cel_default_can_be_removed_on_purpose()
    {
        const string declared = """{"type":"string","default":{"$cel":"now()"}}""";
        var editor = FieldFacets.Prefill("stamp", declared);
        editor.RemoveUndrawnDefault = true;

        editor.Build("stamp", declared, [], out _)!.ToJsonString().ShouldBe("""{"type":"string"}""");
    }

    [Fact]
    public void Required_beside_a_literal_read_only_is_refused_as_the_apply_refuses_it()
    {
        const string declared = """{"type":"string","maxLength":64,"readOnly":true}""";
        var editor = FieldFacets.Prefill("external_ref", declared);
        editor.Required = true;

        editor.Build("external_ref", declared, [], out var refusal).ShouldBeNull();
        refusal.ShouldNotBeNull().ShouldContain("readOnly");
    }

    [Fact]
    public void Required_beside_read_only_with_a_literal_default_is_what_the_apply_accepts()
    {
        const string declared = """{"type":"string","maxLength":64,"readOnly":true}""";
        var editor = FieldFacets.Prefill("external_ref", declared);
        editor.Required = true;
        editor.Default = "none";

        editor.Build("external_ref", declared, [], out var refusal).ShouldNotBeNull();
        refusal.ShouldBeNull();
    }

    [Fact]
    public void Every_facet_the_editor_does_not_draw_is_named_as_kept()
    {
        var editor = FieldFacets.Prefill(
            "external_ref", """{"type":"string","maxLength":64,"readOnly":true,"description":"From the ERP"}""");

        editor.Notes().Select(note => (note.Facet, note.Fate))
            .ShouldBe([("readOnly", FacetFate.Kept), ("description", FacetFate.Kept)], ignoreOrder: true);
    }

    /// <summary>
    /// A note's value reads as it was written: an apostrophe, an ampersand or an angle bracket in a description is
    /// not drawn as <c>\u0027</c> (the audit ledger's Task 2 minor, final branch review item 4).
    /// </summary>
    [Fact]
    public void A_notes_value_is_shown_as_written_not_escaped_for_html()
    {
        var editor = FieldFacets.Prefill(
            "external_ref", """{"type":"string","description":"The ERP's <id> & code","x-note":{"why":"it's"}}""");

        editor.Notes().Single(note => note.Facet == "description").Value.ShouldBe("\"The ERP's <id> & code\"");
        editor.Notes().Single(note => note.Facet == "x-note").Value.ShouldBe("""{"why":"it's"}""");
    }

    [Fact]
    public void A_json_fields_own_default_is_shown_as_written()
    {
        const string declared = """{"type":"json","default":{"label":"don't"}}""";

        FieldFacets.Prefill("meta", declared).Notes().Single(note => note.Facet == "default").Value
            .ShouldBe("""{"label":"don't"}""");
    }

    [Fact]
    public void A_format_is_named_as_removed_once_the_field_is_no_longer_a_string()
    {
        var editor = FieldFacets.Prefill("contact", """{"type":"string","format":"email"}""");
        editor.Type = FieldType.Text;

        editor.Notes().Single().ShouldBe(
            new FacetNote("format", "\"email\"", FacetFate.Removed, "belongs to a string, which this field no longer is."));
    }

    [Fact]
    public void A_new_ref_says_the_on_delete_it_writes()
        => new FieldFacets { Name = "bike", Type = FieldType.Ref, Target = "bikes" }.Notes()
            .Single().ShouldBe(new FacetNote(
                "onDelete", "\"restrict\"", FacetFate.Written,
                "the schema's default, written for a new ref — choosing another is #265."));

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
