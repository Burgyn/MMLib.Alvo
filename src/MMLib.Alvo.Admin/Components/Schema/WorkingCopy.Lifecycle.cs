using MMLib.Alvo.Descriptor;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* An entity's stateMachine and timers, read whole through the descriptor's own model: the States tab draws them, nothing edits them. */
internal sealed partial class WorkingCopy
{
    /// <summary>The entity as the descriptor's model reads it, or <see langword="null"/> when the working copy has none of that shape.</summary>
    /// <remarks>
    /// Read through <see cref="AlvoDescriptor.Parse"/> — the parser apply uses — over the one entity, wrapped in the smallest
    /// descriptor it accepts, so the tab never has a second reading of the members' shape. A half-edited entity the model
    /// refuses draws as an entity declaring neither member.
    /// </remarks>
    /// <param name="entity">The entity's name in the working copy.</param>
    public EntityDescriptor? EntityModelOf(string entity) => Read(() => EntityModel(_working?["entities"]?[entity], entity));

    private static EntityDescriptor? EntityModel(JsonNode? declared, string entity)
    {
        if (declared is not JsonObject)
        {
            return null;
        }

        var wrapper = new JsonObject
        {
            ["apiVersion"] = "alvo.dev/v1",
            ["name"] = "working-copy",
            ["entities"] = new JsonObject { [entity] = declared.DeepClone() },
        };
        try
        {
            return AlvoDescriptor.Parse(wrapper.ToJsonString()).Entities[entity];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
