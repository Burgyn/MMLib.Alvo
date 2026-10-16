using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>What the field editor's <em>Exclude from event images</em> writes, asserted against the descriptor JSON.</summary>
public class WorkingCopyEventExclusionTests
{
    [Fact]
    public void Excluding_a_field_writes_the_schemas_own_shape()
    {
        var copy = Copy();

        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        Events(copy).ShouldBe("""{"exclude":["status"]}""");
        copy.IsExcludedFromEvents("work_orders", "status").ShouldBeTrue();
        copy.IsExcludedFromEvents("work_orders", "code").ShouldBeFalse();
    }

    [Fact]
    public void Excluding_twice_lists_the_field_once()
    {
        var copy = Copy();

        copy.ExcludeFromEvents("work_orders", "status", excluded: true);
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        Events(copy).ShouldBe("""{"exclude":["status"]}""");
    }

    [Fact]
    public void Putting_the_last_field_back_removes_the_block_rather_than_leaving_it_empty()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        copy.ExcludeFromEvents("work_orders", "status", excluded: false);

        Entity(copy).ContainsKey("events").ShouldBeFalse();
        copy.IsDirty.ShouldBeFalse("undoing the only edit leaves nothing to preview");
    }

    [Fact]
    public void Putting_one_field_back_keeps_the_others_excluded()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);
        copy.ExcludeFromEvents("work_orders", "code", excluded: true);

        copy.ExcludeFromEvents("work_orders", "status", excluded: false);

        Events(copy).ShouldBe("""{"exclude":["code"]}""");
    }

    [Fact]
    public void A_rename_carries_the_exclusion()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        copy.RenameField("work_orders", "status", "state", out var uncarried).ShouldBeNull();

        Events(copy).ShouldBe("""{"exclude":["state"]}""");
        uncarried.ShouldBeEmpty();
    }

    [Fact]
    public void A_removal_names_the_exclusion_as_a_place_that_blocks_it()
    {
        var copy = Copy();
        copy.ExcludeFromEvents("work_orders", "status", excluded: true);

        copy.ReferencesToField("work_orders", "status")
            .ShouldContain(reference => reference.Place == "work_orders.events.exclude" && reference.Blocks);
    }

    private static JsonObject Entity(WorkingCopy copy) => (JsonObject)JsonNode.Parse(copy.Json)!["entities"]!["work_orders"]!;

    private static string Events(WorkingCopy copy) => Entity(copy)["events"]!.ToJsonString();

    private static WorkingCopy Copy()
    {
        var copy = new WorkingCopy();
        copy.Take(
            """
            {
              "apiVersion": "v1",
              "name": "field-service",
              "entities": {
                "work_orders": {
                  "fields": {
                    "status": { "type": "string" },
                    "code": { "type": "string" }
                  }
                }
              }
            }
            """,
            revision: 3);

        return copy;
    }
}
