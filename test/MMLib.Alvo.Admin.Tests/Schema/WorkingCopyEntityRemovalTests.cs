using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// What points at an entity, asked before it is removed — the <see cref="EntityReferences"/> places, inbound
/// (docs/todo-admin.md §8d item 24).
/// </summary>
public class WorkingCopyEntityRemovalTests
{
    [Fact]
    public void A_ref_to_the_entity_blocks_its_removal()
    {
        var copy = new WorkingCopy();
        copy.Take(File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", "field-service", "field-service.alvo.json")), 1);

        copy.ReferencesToEntity("regions")
            .ShouldBe([new DescriptorReference("work_orders.region_id", Blocks: true, EntityRemovalWords.Ref)]);
    }

    /// <summary>
    /// Only what the apply itself would refuse blocks (Task 7 fix round 1, ruling 1): the rollup. A hook's
    /// <c>entity.update</c> is refused whatever it names, and automation is never compiled, so those are named with what
    /// becomes of them, and do not block.
    /// </summary>
    [Fact]
    public void A_rollup_blocks_it_and_actions_and_a_trigger_are_named_with_what_becomes_of_them()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);

        copy.ReferencesToEntity("lines").ShouldBe(
        [
            new DescriptorReference("orders.total rollup", Blocks: true, EntityRemovalWords.Rollup),
            new DescriptorReference("entities.orders.hooks.afterUpdate[0].action", Blocks: false, EntityRemovalWords.HookAction),
            new DescriptorReference("automation.recount.trigger", Blocks: false, EntityRemovalWords.AutomationTrigger),
            new DescriptorReference("automation.recount.actions[0]", Blocks: false, EntityRemovalWords.AutomationAction),
        ], ignoreOrder: true);
    }

    /// <summary>A function's trigger is named with the functions block's own state, not automation's.</summary>
    [Fact]
    public void A_functions_trigger_says_functions_are_not_invoked()
        => EntityRemovalWords.Trigger("functions.recalc.trigger").ShouldBe(EntityRemovalWords.FunctionTrigger);

    /// <summary>
    /// A before-hook's <c>mutate</c> literal and an <c>x-*</c> extension are data and annotations: an object shaped like
    /// an action or a trigger inside them names nothing (ruling 4).
    /// </summary>
    [Fact]
    public void A_mutate_literal_and_an_extension_are_not_named()
    {
        var copy = new WorkingCopy();
        copy.Take(Literals, revision: 1);

        copy.ReferencesToEntity("lines").ShouldBeEmpty();
    }

    /// <summary>The rename's walk leaves the same two alone: a literal written to a record is not a name to carry.</summary>
    [Fact]
    public void A_rename_leaves_a_mutate_literal_and_an_extension_as_they_were()
    {
        var copy = new WorkingCopy();
        copy.Take(Literals, revision: 1);

        copy.RenameEntity("lines", "items").ShouldBeNull();

        var root = JsonNode.Parse(copy.Json)!;
        root["entities"]!["orders"]!["hooks"]!["beforeCreate"]![0]!["mutate"]!["note"]!["entity"]!.GetValue<string>().ShouldBe("lines");
        root["x-notes"]!["trigger"]!["event"]!.GetValue<string>().ShouldBe("entity.lines.created");
    }

    /// <summary>
    /// A ref staged by another tab after the confirm read nothing blocking is caught by the removal itself, which asks
    /// again under the same lock (ruling 3): the entity stays and the ref is named.
    /// </summary>
    [Fact]
    public void A_ref_staged_after_the_confirm_read_stops_the_removal()
    {
        var copy = Copy();
        copy.AddEntity("tickets", scoped: false, audited: false);
        copy.ReferencesToEntity("tickets").ShouldBeEmpty();

        copy.AddField("orders", "ticket_id", new JsonObject { ["type"] = "ref", ["entity"] = "tickets" });
        var inbound = copy.RemoveEntityUnlessReferenced("tickets");

        inbound.ShouldBe([new DescriptorReference("orders.ticket_id", Blocks: true, EntityRemovalWords.Ref)]);
        copy.Entities.ShouldContain("tickets");
    }

    /// <summary>Nothing blocking, and the same call removes it.</summary>
    [Fact]
    public void An_entity_only_named_by_non_blocking_places_is_removed_under_the_lock()
    {
        var copy = Copy();
        copy.RemoveField("orders", "total");

        copy.RemoveEntityUnlessReferenced("lines").ShouldAllBe(reference => !reference.Blocks);
        copy.Entities.ShouldNotContain("lines");
    }

    /// <summary><c>orders.parent_id</c> is a self-ref: it goes with the entity and is not named.</summary>
    [Fact]
    public void A_self_ref_goes_with_the_entity_and_is_not_named()
        => Copy().ReferencesToEntity("orders").ShouldNotContain(reference => reference.Place == "orders.parent_id");

    [Fact]
    public void A_child_that_points_at_the_entity_blocks_it_even_when_the_entity_points_back()
        => Copy().ReferencesToEntity("orders").ShouldContain(new DescriptorReference("lines.order_id", Blocks: true, EntityRemovalWords.Ref));

    [Fact]
    public void Removing_an_entity_nothing_points_at_leaves_the_rest_as_it_was()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);
        copy.AddEntity("tickets", scoped: false, audited: false);

        copy.ReferencesToEntity("tickets").ShouldBeEmpty();
        copy.RemoveEntity("tickets");
        copy.IsDirty.ShouldBeFalse();
    }

    /// <summary>
    /// An applied entity the copy removed is listed, so the schema list can strike it through instead of drawing it as
    /// served (the staged-row rule of <c>Entity.ReadWorking</c>).
    /// </summary>
    [Fact]
    public void An_applied_entity_the_copy_removed_is_listed_as_removed()
    {
        var copy = Copy();
        copy.RemoveEntity("lines");

        copy.RemovedEntities.ShouldBe(["lines"]);
    }

    /// <summary>A renamed entity is moved, not removed: its table comes with it, which <c>renamedFrom</c> says.</summary>
    [Fact]
    public void A_renamed_entity_is_not_listed_as_removed()
    {
        var copy = Copy();
        copy.RenameEntity("lines", "items").ShouldBeNull();

        copy.RemovedEntities.ShouldBeEmpty();
    }

    /// <summary>An entity only the copy declared has no table to drop, so removing it leaves nothing to list.</summary>
    [Fact]
    public void A_pending_entity_removed_again_is_not_listed_as_removed()
    {
        var copy = Copy();
        copy.AddEntity("tickets", scoped: false, audited: false);
        copy.RemoveEntity("tickets");

        copy.RemovedEntities.ShouldBeEmpty();
    }

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);
        return copy;
    }

    private const string Descriptor = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "shop",
          "entities": {
            "orders": {
              "fields": {
                "total": { "type": "decimal", "precision": 10, "scale": 2, "rollup": { "op": "sum", "from": "lines", "field": "amount" } },
                "parent_id": { "type": "ref", "entity": "orders" }
              },
              "hooks": { "afterUpdate": [ { "action": { "type": "entity.update", "entity": "lines", "payload": { "a": 1 } } } ] }
            },
            "lines": {
              "fields": { "order_id": { "type": "ref", "entity": "orders" }, "amount": { "type": "decimal", "precision": 10, "scale": 2 } },
              "hooks": { "afterCreate": [ { "action": { "type": "entity.update", "entity": "lines", "payload": { "a": 1 } } } ] }
            }
          },
          "automation": {
            "recount": {
              "trigger": { "event": "entity.lines.created" },
              "actions": [ { "type": "entity.update", "entity": "lines", "payload": { "a": 1 } } ]
            }
          }
        }
        """;

    /// <summary>An action-shaped <c>mutate</c> literal and a trigger-shaped extension, both naming <c>lines</c>.</summary>
    private const string Literals = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "shop",
          "entities": {
            "orders": {
              "fields": { "note": { "type": "json" } },
              "hooks": { "beforeCreate": [ { "mutate": { "note": { "type": "entity.update", "entity": "lines" } } } ] }
            },
            "lines": { "fields": { "amount": { "type": "decimal", "precision": 10, "scale": 2 } } }
          },
          "x-notes": { "trigger": { "event": "entity.lines.created" } }
        }
        """;
}
