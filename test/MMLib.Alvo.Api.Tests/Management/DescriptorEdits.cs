using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Api.Tests.Management;

/// <summary>
/// The two edits the apply facts make to a descriptor.
/// </summary>
/// <remarks>
/// Written over <see cref="JsonNode"/> rather than over <c>AlvoDescriptor</c>, deliberately: an edit that
/// round-tripped through the typed model would prove the model's round trip, not the API's, and the apply
/// facts assert the text the editor sent comes back unchanged.
/// </remarks>
internal static class DescriptorEdits
{
    /// <summary>Adds one optional string field to an entity — an <c>AddField</c> step, never destructive.</summary>
    /// <param name="descriptorJson">The descriptor to edit.</param>
    /// <param name="entity">The entity to add the field to.</param>
    /// <param name="field">The field's name.</param>
    internal static string AddOptionalTextField(string descriptorJson, string entity, string field)
    {
        var root = JsonNode.Parse(descriptorJson)!.AsObject();
        root["entities"]![entity]!["fields"]!.AsObject()[field] = new JsonObject { ["type"] = "string" };

        return Write(root);
    }

    /// <summary>Removes one entity — a <c>DropEntity</c> step, which is what destructive means.</summary>
    /// <remarks>
    /// <c>managed-fleet</c> declares no <c>ref</c> between its entities, so dropping one leaves nothing
    /// dangling and the descriptor still validates — which is what makes the refusal the fact measures the
    /// destructive guardrail rather than the validator.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor to edit.</param>
    /// <param name="entity">The entity to remove.</param>
    internal static string RemoveEntity(string descriptorJson, string entity)
    {
        var root = JsonNode.Parse(descriptorJson)!.AsObject();
        root["entities"]!.AsObject().Remove(entity);

        return Write(root);
    }

    private static string Write(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
