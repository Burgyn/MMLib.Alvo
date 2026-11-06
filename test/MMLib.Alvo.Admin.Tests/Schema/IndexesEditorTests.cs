using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The Indexes tab's add form (HX-12): <i>Only where</i> and <i>Exclude overlaps</i>, decided in <see cref="IndexDraft"/> so the
/// component only draws it.
/// </summary>
public class IndexesEditorTests
{
    private static readonly EntitySchema _rentals = new()
    {
        Name = "rentals",
        Audit = true,
        Fields =
        [
            Field("bike", FieldType.String, required: true),
            Field("status", FieldType.String, required: true),
            Field("starts_at", FieldType.DateTime, required: true),
            Field("due_at", FieldType.DateTime, required: true),
            Field("returned_at", FieldType.DateTime, required: false),
            Field("pickup_day", FieldType.Date, required: true),
            Field("return_day", FieldType.Date, required: true),
            Field("stamped_at", FieldType.DateTime, required: true) with { ComputedExpression = "starts_at" },
            Field("created_at", FieldType.DateTime, required: true),
        ],
    };

    [Fact]
    public void Exclude_overlaps_offers_only_required_date_and_datetime_fields()
        => IndexDraft.RangeFields(_rentals).ShouldBe(["starts_at", "due_at", "pickup_day", "return_day"]);

    [Fact]
    public void The_range_end_is_offered_from_the_start_s_type_and_never_the_start_itself()
    {
        var draft = new IndexDraft { Excludes = true, From = "starts_at" };

        draft.EndFields(_rentals).ShouldBe(["due_at"]);
    }

    [Fact]
    public void Choosing_exclude_overlaps_builds_an_exclusion_and_never_a_unique_one()
    {
        var draft = new IndexDraft { Chosen = ["bike"], Unique = true, Excludes = true, From = "starts_at", To = "due_at", Where = " status != 'cancelled' " };

        var built = draft.Build(out var refusal).ShouldNotBeNull();

        refusal.ShouldBeNull();
        built.Unique.ShouldBeFalse();
        built.Exclude.ShouldBe(new ExclusionSchema("starts_at", "due_at"));
        built.Where.ShouldBe("status != 'cancelled'");
        draft.UniqueOffered.ShouldBeFalse();
    }

    [Fact]
    public void An_exclusion_without_both_ends_is_refused()
    {
        new IndexDraft { Chosen = ["bike"], Excludes = true, From = "starts_at" }.Build(out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain("ends");
    }

    [Fact]
    public void An_empty_where_is_no_where()
        => new IndexDraft { Chosen = ["bike"], Unique = true, Where = "  " }.Build(out _).ShouldNotBeNull().Where.ShouldBeNull();

    [Fact]
    public void A_where_over_the_schema_s_limit_is_refused()
    {
        new IndexDraft { Chosen = ["bike"], Where = new string('x', 2001) }.Build(out var refusal).ShouldBeNull();

        refusal.ShouldNotBeNull().ShouldContain("2000");
    }

    /// <summary>
    /// The box is checked live: the index the form would add is appended, with the typed filter in place, to a clone the
    /// check endpoint judges at that entry's own <c>where</c> — so a name that is not one of the entity's fields is said
    /// under the box, in the validator's words.
    /// </summary>
    [Fact]
    public void Only_where_is_checked_live_against_the_entity_s_fields()
    {
        const string working = """
            {"apiVersion":"v1","name":"shop","entities":{"rentals":{"fields":{"bike":{"type":"string"}},"indexes":[{"fields":["bike"]}]}}}
            """;
        var draft = new IndexDraft { Chosen = ["bike"], Excludes = true, From = "starts_at", To = "due_at" };

        var (json, path) = ExpressionSlots.ForIndexWhere(working, "rentals", draft.Candidate(), "missing == 1").ShouldNotBeNull();

        path.ShouldBe("/entities/rentals/indexes/1/where");
        var placed = JsonNode.Parse(json)!["entities"]!["rentals"]!["indexes"]![1]!;
        placed["where"]!.GetValue<string>().ShouldBe("missing == 1");
        placed["exclude"]!["from"]!.GetValue<string>().ShouldBe("starts_at");
        working.ShouldNotContain("missing");
    }

    [Fact]
    public void An_unknown_entity_has_no_slot_to_check()
        => ExpressionSlots.ForIndexWhere("""{"entities":{}}""", "rentals", new IndexDraft { Chosen = ["bike"] }.Candidate(), "true")
            .ShouldBeNull();

    private static FieldSchema Field(string name, FieldType type, bool required) => new() { Name = name, Type = type, Required = required };
}
