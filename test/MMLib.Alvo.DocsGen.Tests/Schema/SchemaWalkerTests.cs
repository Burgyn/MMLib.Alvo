using MMLib.Alvo.DocsGen.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Tests.Schema;

public class SchemaWalkerTests
{
    private readonly SchemaWalker _walker = new(SchemaFixture.Load());

    [Fact]
    public void Top_level_scalars_and_blocks_are_told_apart()
    {
        _walker.IsBlock("name").ShouldBeFalse();
        _walker.IsBlock("items").ShouldBeTrue();
    }

    [Fact]
    public void A_map_key_is_documented_through_its_entries()
    {
        var keys = _walker.KeysOf("items");

        keys.Select(key => key.Path).ShouldBe(
        [
            "items",
            "items.<name>.kind",
            "items.<name>.size",
            "items.<name>.child",
            "items.<name>.steps",
            "items.<name>.steps[].type",
            "items.<name>.steps[].url",
            "items.<name>.steps[].body",
        ]);
        var items = keys[0];
        items.Type.ShouldBe("map of object");
        items.Notes.ShouldContain("Each entry: One item.");
        items.Notes.ShouldContain("Names match `^[a-z_]+$`.");
        items.Notes.ShouldContain("Reserved name: `users` is not allowed.");
    }

    [Fact]
    public void Anchors_drop_placeholders_and_are_unique()
    {
        var keys = _walker.KeysOf("items");

        Key(keys, "items.<name>.kind").Anchor.ShouldBe("items.kind");
        keys.Select(key => key.Anchor).ShouldBeUnique();
    }

    [Fact]
    public void A_repeated_ref_is_expanded_once()
    {
        var keys = _walker.KeysOf("items");

        Key(keys, "items.<name>.child").Notes.ShouldContain("Same shape as [`items.<name>`](#items).");
        keys.ShouldNotContain(key => key.Path.StartsWith("items.<name>.child.", StringComparison.Ordinal));
    }

    [Fact]
    public void Oneof_variants_are_merged()
    {
        var keys = _walker.KeysOf("items");

        Key(keys, "items.<name>.steps[].type").Values.ShouldBe(["\"x\"", "\"y\""]);
        keys.Count(key => key.Path == "items.<name>.steps[].url").ShouldBe(1);
        Key(keys, "items.<name>.steps[].url").Description.ShouldBe("Target.");
    }

    [Fact]
    public void A_key_only_some_variants_declare_names_its_variant()
    {
        var keys = _walker.KeysOf("items");

        Key(keys, "items.<name>.steps[].body").Notes.ShouldContain("Only when `type` is `\"y\"`.");
        Key(keys, "items.<name>.steps[].url").Notes.ShouldBeEmpty();
    }

    [Fact]
    public void Undiscriminated_variants_are_named_as_alternatives()
    {
        var walker = new SchemaWalker(Parse(Alternatives));

        Key(walker.KeysOf("action"), "action.reject").Notes.ShouldContain("Exactly one of `reject`, `mutate`.");
    }

    [Fact]
    public void Variants_that_disagree_are_described_one_by_one()
    {
        var keys = new SchemaWalker(Parse(DisagreeingVariants)).KeysOf("action");
        var payload = Key(keys, "action.payload");

        payload.Description.ShouldNotBe("A text.");
        payload.Required.ShouldBeFalse();
        payload.RequiredDetail.ShouldBe("depends on the variant");
        payload.Notes.ShouldContain("When `type` is `\"a\"` or `\"c\"`: `string`, optional — A text.");
        payload.Notes.ShouldContain("When `type` is `\"b\"`: `map of integer`, required — A map. Each entry: A number.");
    }

    [Fact]
    public void A_discriminator_without_a_description_selects_the_variant() =>
        Key(new SchemaWalker(Parse(DisagreeingVariants)).KeysOf("action"), "action.type").Description.ShouldBe("Selects the variant.");

    [Fact]
    public void A_key_required_only_in_its_variant_says_so()
    {
        var walker = new SchemaWalker(Parse(Alternatives));

        Key(walker.KeysOf("action"), "action.reject").RequiredDetail.ShouldBe("yes (in its variant)");
    }

    [Fact]
    public void A_negated_const_with_required_reads_required_unless()
    {
        var walker = new SchemaWalker(Parse("""
            { "properties": { "rollup": { "type": "object",
              "properties": { "op": { "enum": ["sum", "count"] }, "field": { "type": "string", "description": "F." } },
              "allOf": [ { "if": { "not": { "properties": { "op": { "const": "count" } } } }, "then": { "required": ["field"] } } ] } } }
            """));

        Key(walker.KeysOf("rollup"), "rollup.field").Notes.ShouldContain("Required unless `op` is `\"count\"`.");
        walker.UnrecognisedConditions.ShouldBeEmpty();
    }

    [Fact]
    public void Recognised_conditions_become_notes()
    {
        var keys = _walker.KeysOf("items");

        Key(keys, "items.<name>.size").Notes.ShouldContain("Required when `kind` is `\"b\"`.");
        Key(keys, "items.<name>.size").Notes.ShouldContain("Allowed only when `kind` is `\"b\"`.");
        Key(keys, "items.<name>.steps").Notes.ShouldContain("Not allowed together with `child`.");
    }

    [Fact]
    public void An_unrecognised_condition_is_reported()
    {
        _walker.KeysOf("items");

        _walker.UnrecognisedConditions.ShouldHaveSingleItem().ShouldContain("items.<name>");
    }

    [Fact]
    public void Required_and_defaults_are_read()
    {
        var kind = Key(_walker.KeysOf("items"), "items.<name>.kind");

        kind.Required.ShouldBeTrue();
        kind.Default.ShouldBe("\"a\"");
        kind.Values.ShouldBe(["\"a\"", "\"b\""]);
    }

    internal const string Alternatives = """
        { "properties": { "action": { "oneOf": [
          { "type": "object", "required": ["reject"], "properties": { "reject": { "type": "string", "description": "R." } } },
          { "type": "object", "required": ["mutate"], "properties": { "mutate": { "type": "object", "description": "M." } } } ] } } }
        """;

    private const string DisagreeingVariants = """
        { "properties": { "action": { "oneOf": [
          { "type": "object", "required": ["type"], "properties": { "type": { "const": "a" }, "payload": { "type": "string", "description": "A text." } } },
          { "type": "object", "required": ["type", "payload"], "properties": { "type": { "const": "b" },
            "payload": { "type": "object", "description": "A map.", "additionalProperties": { "type": "integer", "description": "A number." } } } },
          { "type": "object", "required": ["type"], "properties": { "type": { "const": "c" }, "payload": { "type": "string", "description": "A text." } } } ] } } }
        """;

    internal static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static SchemaKey Key(IReadOnlyList<SchemaKey> keys, string path) => keys.Single(key => key.Path == path);
}
