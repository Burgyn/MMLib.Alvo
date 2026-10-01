using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// Renaming an entity carries every place the descriptor names it, so the renamed copy is one the apply accepts.
/// </summary>
public class WorkingCopyEntityReferenceTests
{
    /// <summary>
    /// The field-service example: <c>work_orders.region_id</c> follows <c>regions</c> to its new name.
    /// </summary>
    /// <remarks>
    /// Two entities differ from the applied revision afterwards, the renamed one and the one whose ref moved,
    /// and the count says two; renaming back leaves nothing pending at all, refs included.
    /// </remarks>
    [Fact]
    public void Renaming_a_referenced_entity_repoints_its_inbound_refs()
    {
        var copy = new WorkingCopy();
        copy.Take(File.ReadAllText(FieldService), revision: 1);

        copy.RenameEntity("regions", "service_areas").ShouldBeNull();

        Node(copy)["entities"]!["work_orders"]!["fields"]!["region_id"]!["entity"]!.GetValue<string>()
            .ShouldBe("service_areas");
        copy.Json.ShouldNotContain("\"entity\": \"regions\"");
        copy.PendingCount.ShouldBe(2, "the renamed entity, and the entity whose ref now names it");

        copy.RenameEntity("service_areas", "regions").ShouldBeNull();

        copy.IsDirty.ShouldBeFalse();
        copy.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void A_rollup_an_action_and_a_trigger_follow_the_rename_and_a_payload_and_an_expression_do_not()
    {
        var copy = new WorkingCopy();
        copy.Take(Descriptor, revision: 2);

        copy.RenameEntity("lines", "order_lines").ShouldBeNull();

        var root = Node(copy);
        var total = root["entities"]!["orders"]!["fields"]!["total"]!["rollup"]!;
        total["from"]!.GetValue<string>().ShouldBe("order_lines");
        total["via"]!.GetValue<string>().ShouldBe("order_id", "via names a field, not an entity");

        var action = root["entities"]!["orders"]!["hooks"]!["afterUpdate"]![0]!["action"]!;
        action["entity"]!.GetValue<string>().ShouldBe("order_lines");
        action["payload"]!["note"]!.GetValue<string>().ShouldBe("entity.lines.updated");

        var rule = root["automation"]!["recount"]!;
        rule["trigger"]!["event"]!.GetValue<string>().ShouldBe("entity.order_lines.created");
        rule["condition"]!.GetValue<string>().ShouldBe("event.entity == 'lines'");

        root["automation"]!["unrelated"]!["trigger"]!["event"]!.GetValue<string>()
            .ShouldBe("entity.lines_archive.created");
    }

    private static JsonNode Node(WorkingCopy copy) => JsonNode.Parse(copy.Json)!;

    private static string FieldService { get; } =
        Path.Combine(RepositoryRoot.Find(), "examples", "field-service", "field-service.alvo.json");

    private const string Descriptor = """
        {
          "apiVersion": "v1",
          "name": "shop",
          "entities": {
            "orders": {
              "fields": {
                "total": { "type": "decimal", "rollup": { "op": "sum", "from": "lines", "field": "amount", "via": "order_id" } }
              },
              "hooks": {
                "afterUpdate": [
                  { "action": { "type": "entity.update", "entity": "lines", "payload": { "note": "entity.lines.updated" } } }
                ]
              }
            },
            "lines": {
              "fields": {
                "order_id": { "type": "ref", "entity": "orders" },
                "amount": { "type": "decimal" }
              }
            },
            "lines_archive": { "fields": { "amount": { "type": "decimal" } } }
          },
          "automation": {
            "recount": {
              "trigger": { "event": "entity.lines.created" },
              "condition": "event.entity == 'lines'",
              "actions": []
            },
            "unrelated": {
              "trigger": { "event": "entity.lines_archive.created" },
              "actions": []
            }
          }
        }
        """;
}
