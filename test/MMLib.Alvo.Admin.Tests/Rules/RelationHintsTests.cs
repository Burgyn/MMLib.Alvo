using MMLib.Alvo.Admin.Components.Rules;

namespace MMLib.Alvo.Admin.Tests.Rules;

/// <summary>The relation terms the rules editor offers are the ones apply would compile, spelled the way it wants them.</summary>
public sealed class RelationHintsTests
{
    private const string Descriptor = """
        {
          "entities": {
            "customers": { "fields": { "owner_id": { "type": "uuid" } } },
            "tags": { "fields": { "label": { "type": "string" } } },
            "archive": { "storage": "dynamic", "fields": { "order_id": { "type": "ref", "entity": "orders" } } },
            "orders": {
              "fields": {
                "customer_id": { "type": "ref", "entity": "customers" },
                "owner": { "type": "ref", "entity": "users" },
                "archive_id": { "type": "ref", "entity": "archive" },
                "ghost_id": { "type": "ref", "entity": "ghosts" },
                "note": { "type": "string" }
              }
            },
            "order_notes": { "fields": { "order_id": { "type": "ref", "entity": "orders" } } },
            "transfers": {
              "fields": {
                "from_order": { "type": "ref", "entity": "orders" },
                "to_order": { "type": "ref", "entity": "orders" }
              }
            }
          }
        }
        """;

    [Fact]
    public void Every_forward_ref_and_reverse_child_is_offered_and_an_ambiguous_child_only_in_via_form()
    {
        var hints = RelationHints.For(Descriptor, "orders");

        hints.Select(hint => hint.Term).ShouldBe(
        [
            "customer_id.<field>",
            "order_notes.exists(x, x.<field> …)",
            "transfers_via_from_order.exists(x, x.<field> …)",
            "transfers_via_to_order.exists(x, x.<field> …)",
        ]);
        hints.Select(hint => hint.Entity).ShouldBe(["customers", "order_notes", "transfers", "transfers"]);
    }

    [Fact]
    public void An_entity_with_no_relation_text_that_is_no_descriptor_and_an_unknown_entity_offer_nothing()
    {
        RelationHints.For(Descriptor, "transfers").Select(hint => hint.Term).ShouldBe(
            ["from_order.<field>", "to_order.<field>"]);
        RelationHints.For(Descriptor, "tags").ShouldBeEmpty();
        RelationHints.For("not json", "orders").ShouldBeEmpty();
        RelationHints.For(Descriptor, "nobody").ShouldBeEmpty();
    }
}
