using MMLib.Alvo.DocsGen.Schema;

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
        var walker = new SchemaWalker(System.Text.Json.Nodes.JsonNode.Parse("""
            { "properties": { "action": { "oneOf": [
              { "type": "object", "required": ["reject"], "properties": { "reject": { "type": "string" } } },
              { "type": "object", "required": ["mutate"], "properties": { "mutate": { "type": "object" } } } ] } } }
            """)!.AsObject());

        Key(walker.KeysOf("action"), "action.reject").Notes.ShouldContain("Exactly one of `reject`, `mutate`.");
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

    private static SchemaKey Key(IReadOnlyList<SchemaKey> keys, string path) => keys.Single(key => key.Path == path);
}
