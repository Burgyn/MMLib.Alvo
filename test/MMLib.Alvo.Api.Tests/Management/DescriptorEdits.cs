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

    /// <summary>
    /// Rewrites the <c>access</c> block so the named role administers the project and the existing
    /// <c>viewer</c> level names nobody — the escalation C-1 reproduced.
    /// </summary>
    /// <param name="descriptorJson">The descriptor to edit.</param>
    /// <param name="role">The role to hand administration to.</param>
    internal static string GrantAdminTo(string descriptorJson, string role)
    {
        var root = JsonNode.Parse(descriptorJson)!.AsObject();
        var access = root["access"]!.AsObject();
        access["admin"] = $"'{role}' in @user.roles";
        access.Remove("viewer");

        return Write(root);
    }

    /// <summary>
    /// Points the <c>viewer</c> level at a different role, leaving <c>developer</c> and <c>admin</c> alone.
    /// </summary>
    /// <remarks>
    /// The edit a rollback fact needs: it puts a <em>different</em> <c>access</c> block into the history
    /// without changing which caller is the developer or the administrator, so a later rollback across it is
    /// measurably an access change and both callers still hold the level the fact addresses them with.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor to edit.</param>
    /// <param name="role">The role that may view after the edit.</param>
    internal static string GrantViewTo(string descriptorJson, string role)
    {
        var root = JsonNode.Parse(descriptorJson)!.AsObject();
        root["access"]!.AsObject()["viewer"] = $"'{role}' in @user.roles";

        return Write(root);
    }

    /// <summary>
    /// Re-emits the descriptor with different whitespace and nothing else.
    /// </summary>
    /// <remarks>
    /// It is what holds the escalation guard to "compare the parsed blocks": a reformatted
    /// <c>access</c> block is the same block, so a guard that compared raw text would refuse a developer
    /// for changing nothing.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor to re-emit.</param>
    internal static string Reformat(string descriptorJson) =>
        Write(JsonNode.Parse(descriptorJson)!.AsObject());

    private static string Write(JsonObject root) =>
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}
