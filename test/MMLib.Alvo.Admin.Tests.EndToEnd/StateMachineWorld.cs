using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world with a state machine and a timer on work orders, patched over the example rather than copied
/// from it, so the example stays the one source of the rest.
/// </summary>
public sealed class StateMachineWorld : AdminWorld
{
    /// <inheritdoc/>
    protected override string Descriptor { get; } = WithLifecycle(Descriptors.FieldService);

    private static string WithLifecycle(string descriptor)
    {
        var root = JsonNode.Parse(descriptor)!;
        var orders = root["entities"]!["work_orders"]!.AsObject();
        orders["stateMachine"] = JsonNode.Parse(
            """
            {"field": "status", "initial": "scheduled", "transitions": {
              "start": {"from": ["scheduled"], "to": "in_progress"},
              "complete": {"from": ["in_progress"], "to": "completed", "roles": ["technician"]},
              "cancel": {"from": ["scheduled", "in_progress"], "to": "cancelled", "roles": ["dispatcher"], "when": "new.scheduled_for > now()"}}}
            """);
        orders["timers"] = JsonNode.Parse(
            """{"visit_reminder": {"at": "scheduled_for - duration('2h')", "when": "status == 'scheduled'"}}""");
        return root.ToJsonString();
    }
}
