using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// Renaming a field carries every place the descriptor names it — composite indexes, a child rollup's field and
/// via, CEL in rules/computed/hooks, mutate keys, <c>{{new.…}}</c> placeholders — and a removal names them first.
/// Modelled on <see cref="WorkingCopyEntityReferenceTests"/>.
/// </summary>
public class WorkingCopyFieldReferenceTests
{
    [Fact]
    public void A_rename_carries_indexes_rules_hooks_mutate_keys_and_placeholders()
    {
        var copy = Copy();

        copy.RenameField("orders", "status", "state", out var uncarried).ShouldBeNull();

        var orders = Node(copy)["entities"]!["orders"]!;
        orders["indexes"]![0]!["fields"]!.ToJsonString().ShouldBe("""["state","total"]""");
        orders["rules"]!["list"]!.GetValue<string>().ShouldBe("state == 'open' || 'status' in @user.roles");
        orders["rules"]!["get"]!.GetValue<string>().ShouldBe("@user.status == 'x'");
        var before = orders["hooks"]!["beforeUpdate"]![0]!;
        before["condition"]!.GetValue<string>().ShouldBe("changed(state) && new.state == 'closed'");
        before["action"]!["mutate"]!["state"]!["$cel"]!.GetValue<string>().ShouldBe("lowerAscii(new.state)");
        orders["hooks"]!["afterUpdate"]![0]!["action"]!["to"]!.GetValue<string>().ShouldBe("{{ new.state }}@example.com");
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void A_rename_on_the_child_carries_the_parents_rollup_field_and_via()
    {
        var copy = Copy();

        copy.RenameField("lines", "amount", "line_amount").ShouldBeNull();
        copy.RenameField("lines", "order_id", "order").ShouldBeNull();

        var rollup = Node(copy)["entities"]!["orders"]!["fields"]!["total"]!["rollup"]!;
        rollup["field"]!.GetValue<string>().ShouldBe("line_amount");
        rollup["via"]!.GetValue<string>().ShouldBe("order");
    }

    [Fact]
    public void A_rename_carries_a_computed_expression_that_names_it()
    {
        var copy = Copy();

        copy.RenameField("orders", "total", "grand_total").ShouldBeNull();

        Node(copy)["entities"]!["orders"]!["fields"]!["double_total"]!["computed"]!.GetValue<string>()
            .ShouldBe("grand_total + grand_total");
    }

    [Fact]
    public void An_expression_the_rename_cannot_rewrite_is_named_and_left_as_written()
    {
        var copy = Copy();

        copy.RenameField("orders", "flag", "marker", out var uncarried).ShouldBeNull();

        uncarried.ShouldBe([new DescriptorReference("orders.rules.delete", Blocks: false)]);
        Node(copy)["entities"]!["orders"]!["rules"]!["delete"]!.GetValue<string>().ShouldBe("tags.exists(t, t == flag)");
    }

    [Fact]
    public void A_removal_names_every_place_that_names_the_field_and_which_of_them_block_it()
    {
        var references = Copy().ReferencesToField("orders", "status");

        references.ShouldContain(new DescriptorReference("orders.indexes[0]", Blocks: true));
        references.ShouldContain(new DescriptorReference("orders.rules.list", Blocks: true));
        references.ShouldContain(new DescriptorReference("orders.hooks.beforeUpdate[0].mutate", Blocks: true));
        references.ShouldNotContain(reference => reference.Place == "orders.rules.get");
    }

    [Fact]
    public void A_child_fields_removal_names_the_rollup_that_aggregates_it()
        => Copy().ReferencesToField("lines", "amount")
            .ShouldBe([new DescriptorReference("orders.fields.total.rollup.field", Blocks: true)]);

    [Fact]
    public void Asking_what_names_a_field_changes_nothing()
    {
        var copy = Copy();
        var before = copy.Json;

        copy.ReferencesToField("orders", "status");

        copy.Json.ShouldBe(before);
    }

    [Fact]
    public void A_field_nothing_names_has_no_references()
        => Copy().ReferencesToField("orders", "note").ShouldBeEmpty();

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 3);
        return copy;
    }

    private static JsonNode Node(WorkingCopy copy) => JsonNode.Parse(copy.Json)!;

    private const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "shop",
          "entities": {
            "orders": {
              "fields": {
                "status": { "type": "string" },
                "flag": { "type": "boolean" },
                "note": { "type": "text" },
                "total": { "type": "decimal", "precision": 10, "scale": 2, "rollup": { "from": "lines", "op": "sum", "field": "amount", "via": "order_id" } },
                "double_total": { "type": "decimal", "precision": 12, "scale": 2, "computed": "total + total" }
              },
              "indexes": [ { "fields": ["status", "total"] } ],
              "rules": {
                "list": "status == 'open' || 'status' in @user.roles",
                "get": "@user.status == 'x'",
                "delete": "tags.exists(t, t == flag)"
              },
              "hooks": {
                "beforeUpdate": [ { "condition": "changed(status) && new.status == 'closed'", "action": { "mutate": { "status": { "$cel": "lowerAscii(new.status)" } } } } ],
                "afterUpdate": [ { "action": { "type": "email", "template": "notice", "to": "{{ new.status }}@example.com" } } ]
              }
            },
            "lines": {
              "fields": {
                "order_id": { "type": "ref", "entity": "orders" },
                "amount": { "type": "decimal", "precision": 10, "scale": 2 }
              }
            }
          }
        }
        """;
}
