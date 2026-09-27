using MMLib.Alvo.Admin.Components.Schema;

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

        copy.ReferencesToEntity("regions").ShouldBe([new DescriptorReference("work_orders.region_id", Blocks: true)]);
    }

    [Fact]
    public void A_rollup_and_an_entity_update_block_it_and_a_trigger_is_named()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 1);

        copy.ReferencesToEntity("lines").ShouldBe(
        [
            new DescriptorReference("orders.total rollup", Blocks: true),
            new DescriptorReference("entities.orders.hooks.afterUpdate[0].action", Blocks: true),
            new DescriptorReference("automation.recount.trigger", Blocks: false),
        ], ignoreOrder: true);
    }

    /// <summary><c>orders.parent_id</c> is a self-ref: it goes with the entity and is not named.</summary>
    [Fact]
    public void A_self_ref_goes_with_the_entity_and_is_not_named()
        => Copy().ReferencesToEntity("orders").ShouldNotContain(reference => reference.Place == "orders.parent_id");

    [Fact]
    public void A_child_that_points_at_the_entity_blocks_it_even_when_the_entity_points_back()
        => Copy().ReferencesToEntity("orders").ShouldContain(new DescriptorReference("lines.order_id", Blocks: true));

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
          "automation": { "recount": { "trigger": { "event": "entity.lines.created" }, "actions": [] } }
        }
        """;
}
