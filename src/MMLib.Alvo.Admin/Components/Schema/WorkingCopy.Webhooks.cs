using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The webhooks block: declaring and editing an endpoint, through Edit like every other block. */
internal sealed partial class WorkingCopy
{
    /// <summary>
    /// Declares a webhook endpoint, or edits a declared one in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Re-checked under the gate</b> (spec §5.5): a new name another tab declared meanwhile, or an edited one it removed,
    /// is refused rather than overwritten or created. A block of the wrong shape (<c>webhooks</c> or <c>endpoints</c> not an
    /// object) is left for the apply to refuse; replacing it would silently drop what the author wrote.
    /// </para>
    /// <para>
    /// An edit patches the existing object, so a key the sheet does not draw stays; a new one is written in the schema's
    /// order — <c>url</c>, <c>secretRef</c>, <c>description</c>.
    /// </para>
    /// </remarks>
    /// <param name="name">The endpoint's key.</param>
    /// <param name="url">The URL deliveries are posted to.</param>
    /// <param name="secretRef">The name the signing secret will be stored under — never a secret.</param>
    /// <param name="description">The description, or blank for none.</param>
    /// <param name="editing">Whether an existing endpoint is edited.</param>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool DeclareEndpoint(string name, string url, string secretRef, string? description, bool editing) => Edit(root =>
    {
        if (!Settable(root["webhooks"]) || (root["webhooks"] is JsonObject webhooks && !Settable(webhooks["endpoints"])))
        {
            return false;
        }

        var current = (root["webhooks"]?["endpoints"] as JsonObject)?[name];
        if ((current is not null) != editing || (editing && current is not JsonObject))
        {
            return false;
        }

        var endpoint = current as JsonObject ?? new JsonObject();
        endpoint["url"] = url;
        endpoint["secretRef"] = secretRef;
        SetOrRemove(endpoint, "description", description);
        if (!editing)
        {
            Ensure(Ensure(root, "webhooks"), "endpoints")[name] = endpoint;
        }

        return true;
    });

    /// <summary>Whether a block may be written into: absent, or an object.</summary>
    private static bool Settable(JsonNode? node) => node is null or JsonObject;

    /// <summary>Sets a text key, or removes it when the text is blank.</summary>
    private static void SetOrRemove(JsonObject owner, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            owner.Remove(key);
        }
        else
        {
            owner[key] = value;
        }
    }
}
