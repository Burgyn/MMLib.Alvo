using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The field-service world without <c>visits</c>, the <c>on_site_minutes</c> rollup over it and the billing automation
/// triggered by <c>work_orders</c> updates, patched over the example rather than copied from it, so <c>work_orders</c> is
/// again an applied entity that points at others and that nothing names.
/// </summary>
/// <remarks>
/// In the example itself every entity is pointed at — <c>visits</c> refers to <c>work_orders</c>, and
/// <c>work_orders.on_site_minutes</c> rolls <c>visits</c> up — so no applied entity can be removed there at all, and the
/// removal scenarios need one that can. The automation goes too: a rule whose trigger names a removed entity refuses the
/// descriptor, so with it the removal would never reach a plan.
/// </remarks>
public sealed class LeafWorkOrdersWorld : AdminWorld
{
    /// <inheritdoc/>
    protected override string Descriptor { get; } = WithoutVisits(Descriptors.FieldService);

    private static string WithoutVisits(string descriptor)
    {
        var root = JsonNode.Parse(descriptor)!;
        root["entities"]!.AsObject().Remove("visits");
        root["entities"]!["work_orders"]!["fields"]!.AsObject().Remove("on_site_minutes");
        root.AsObject().Remove("automation");
        return root.ToJsonString();
    }
}
