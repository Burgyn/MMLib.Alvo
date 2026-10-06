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
    /// is refused rather than overwritten or created. An edit also acts only on what it named, as <see cref="ReplaceHook"/>
    /// does (§5.1, Ruling V-B): given the endpoint as the sheet drew it when it opened (<see cref="EndpointAsDrawn"/>), it
    /// is refused when the declaration now reads otherwise — a URL another tab or the assistant changed meanwhile would
    /// otherwise be put back without a word, and the URL is where the whole row is posted. A block of the wrong shape (<c>webhooks</c> or <c>endpoints</c> not an
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
    /// <param name="drawn">For an edit, the endpoint as the sheet drew it when it opened; <see langword="null"/> checks only that it exists.</param>
    /// <returns><see langword="true"/> when it was written.</returns>
    public bool DeclareEndpoint(string name, string url, string secretRef, string? description, bool editing, string? drawn = null)
        => Edit(root =>
    {
        if (!Settable(root["webhooks"]) || (root["webhooks"] is JsonObject webhooks && !Settable(webhooks["endpoints"])))
        {
            return false;
        }

        var current = (root["webhooks"]?["endpoints"] as JsonObject)?[name];
        if ((current is not null) != editing || (editing && current is not JsonObject) || ChangedSinceDrawn(current, drawn))
        {
            return false;
        }

        var endpoint = current as JsonObject ?? new JsonObject();
        endpoint["url"] = url;
        endpoint["secretRef"] = secretRef;
        SetOrRemoveBlank(endpoint, "description", description);
        if (!editing)
        {
            Ensure(Ensure(root, "webhooks"), "endpoints")[name] = endpoint;
        }

        return true;
    });

    /// <summary>
    /// The endpoint <paramref name="name"/> as an editor draws it — the text <see cref="DeclareEndpoint"/>'s guard compares —
    /// or <see langword="null"/> when nothing declares it.
    /// </summary>
    /// <param name="name">The endpoint's key.</param>
    public string? EndpointAsDrawn(string name)
        => Read(() => _working?["webhooks"]?["endpoints"]?[name] is { } endpoint ? Readable(endpoint, "{}") : null);

    /// <summary>Whether a declaration an edit opened now reads otherwise than it did — never for a guard not given.</summary>
    /// <param name="current">The declaration in the copy now.</param>
    /// <param name="drawn">The declaration as the sheet drew it, or <see langword="null"/>.</param>
    private static bool ChangedSinceDrawn(JsonNode? current, string? drawn)
        => drawn is not null && !string.Equals(Readable(current, "{}"), drawn, StringComparison.Ordinal);

    /// <summary>Whether a block may be written into: absent, or an object.</summary>
    private static bool Settable(JsonNode? node) => node is null or JsonObject;

    /// <summary>Sets a text key, or removes it when the text is blank — empty or whitespace only.</summary>
    /// <remarks>
    /// <para>
    /// Blank, not merely empty: a key holding only whitespace says nothing a reader can use, so it is not written. This
    /// differs on purpose from <c>FieldFacets.SetOrRemove</c>, which removes only a null or empty value — its callers pass
    /// a chosen option or <see langword="null"/>, never typed text — which is why the two are not one helper.
    /// </para>
    /// <para>
    /// The value is written as given; the callers trim first. <c>EndpointEditor.SaveAsync</c> trims the description, and
    /// the template sheet must trim the subject the same way, so a kept value never carries the padding it was typed with.
    /// </para>
    /// </remarks>
    private static void SetOrRemoveBlank(JsonObject owner, string key, string? value)
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
