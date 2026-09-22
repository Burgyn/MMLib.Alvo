using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The descriptor an operator is editing, before it is applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>One applied document, one working document, and the diff between them.</b> Every edit in the
/// dashboard — a field, a rule, a role, a level — lands in the same working document, because
/// there is one descriptor and one apply. Three separate edit queues with three separate
/// "unsaved" badges would be three ways to describe one pending change (§4.5).
/// </para>
/// <para>
/// <b>It mutates the stored JSON, never a projection of it.</b> Design §6.3 criterion 3: after any
/// UI change, <c>GET descriptor</c> must equal what the editor sent. A round trip through typed
/// objects silently drops every key the projection does not know about — a <c>webhooks</c> block
/// the editor has no screen for would vanish the first time somebody renamed a field. So this
/// holds a <see cref="JsonNode"/> and edits nodes.
/// </para>
/// <para>
/// <b>Scoped to the circuit.</b> An operator's unapplied edits are theirs; another operator's
/// apply moves the revision, and this copy's next apply is then refused by <c>If-Match</c> rather
/// than overwriting them.
/// </para>
/// </remarks>
internal sealed class WorkingCopy
{
    /// <summary>
    /// How the working document is written back out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The encoder is the load-bearing half.</b> <c>System.Text.Json</c>'s default escapes
    /// anything that could be dangerous in HTML — including the apostrophe, as <c>\u0027</c>. A CEL
    /// rule is mostly apostrophes: <c>'dispatcher' in @user.roles</c> comes back as
    /// <c>\u0027dispatcher\u0027 in @user.roles</c>. The descriptor still round-trips — JSON
    /// unescapes to the same string, and the apply is unaffected — but the file a person reads,
    /// commits and diffs has been mangled by a control that was only asked to add a rule. Relaxed
    /// escaping keeps it readable.
    /// </para>
    /// <para>
    /// <b>Relaxed escaping is safe here because nothing renders this as markup.</b> The descriptor
    /// reaches a screen through a component that HTML-encodes it first, and it reaches the apply as
    /// a request body. The name of the option warns about the case where neither is true.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions _pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private JsonNode? _applied;
    private JsonNode? _working;

    /// <summary>The revision the working copy was taken from.</summary>
    public int Revision { get; private set; }

    /// <summary>Whether a working copy has been loaded at all.</summary>
    public bool Loaded => _working is not null;

    /// <summary>The working document, formatted.</summary>
    public string Json => _working?.ToJsonString(_pretty) ?? "{}";

    /// <summary>The applied document, formatted.</summary>
    public string AppliedJson => _applied?.ToJsonString(_pretty) ?? "{}";

    /// <summary>Whether the working document differs from the applied one.</summary>
    /// <summary>
    /// What the apply should say it was for, when something other than the operator's own typing composed
    /// this copy.
    /// </summary>
    /// <remarks>
    /// <b>A suggestion, never the reason itself.</b> Preview pre-fills its box with it and the operator can
    /// replace every character — which is what keeps the history a record of what a person meant rather than
    /// of what a machine drafted. Cleared by <see cref="Discard"/> along with everything else the copy held.
    /// </remarks>
    public string? SuggestedReason { get; private set; }

    /// <summary>Records what an apply of this copy was asked for.</summary>
    /// <param name="reason">The suggestion, rendered verbatim and parsed by nobody.</param>
    public void SuggestReason(string? reason) => SuggestedReason = reason;

    public bool IsDirty => !string.Equals(Json, AppliedJson, StringComparison.Ordinal);

    /// <summary>The entity names the working document declares.</summary>
    public IReadOnlyList<string> Entities => _working?["entities"] is JsonObject entities
        ? [.. entities.Select(entity => entity.Key)]
        : [];

    /// <summary>Takes a fresh working copy from an applied descriptor.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="revision">The revision it is at.</param>
    public void Take(string descriptorJson, int revision)
    {
        _applied = JsonNode.Parse(descriptorJson);
        _working = JsonNode.Parse(descriptorJson);
        Revision = revision;
    }

    /// <summary>Discards every unapplied edit.</summary>
    public void Discard()
    {
        SuggestedReason = null;
        _working = _applied is null ? null : JsonNode.Parse(_applied.ToJsonString());
    }

    /// <summary>
    /// Replaces the whole working document — the import path.
    /// </summary>
    /// <remarks>
    /// The caller has already decided the text is a descriptor worth previewing. Nothing is
    /// validated here beyond it being JSON, because the authority on whether a descriptor is
    /// acceptable is the apply, and a second validator in the client is a second set of rules to
    /// disagree with. What the import screen does check first is the frozen schema's own name
    /// patterns, because a name the apply will refuse is worth refusing before it is rendered.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor to work from.</param>
    /// <returns><see langword="true"/> when it parsed.</returns>
    public bool Replace(string descriptorJson)
    {
        try
        {
            _working = JsonNode.Parse(descriptorJson);
            return _working is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Adds an entity with one required string field, the smallest thing the schema admits.</summary>
    /// <param name="name">The entity's name.</param>
    /// <param name="scoped">Whether it is tenant-scoped.</param>
    /// <param name="audited">Whether it carries the audit columns.</param>
    public void AddEntity(string name, bool scoped, bool audited)
    {
        var entities = Ensure(_working, "entities");
        entities[name] = new JsonObject
        {
            ["tenancy"] = scoped ? "scoped" : "global",
            ["audit"] = audited,
            ["fields"] = new JsonObject
            {
                ["name"] = new JsonObject
                {
                    ["type"] = "string",
                    ["required"] = true,
                    ["maxLength"] = 120,
                },
            },
        };
    }

    /// <summary>Removes an entity.</summary>
    public void RemoveEntity(string name) => (_working?["entities"] as JsonObject)?.Remove(name);

    /// <summary>Adds a field to an entity.</summary>
    /// <param name="entity">The entity to add it to.</param>
    /// <param name="name">The field's name.</param>
    /// <param name="facets">Its type and facets, already in the schema's own shape.</param>
    public void AddField(string entity, string name, JsonObject facets)
    {
        if (_working?["entities"]?[entity] is JsonObject declared)
        {
            var fields = Ensure(declared, "fields");
            fields[name] = facets;
        }
    }

    /// <summary>Removes a field from an entity.</summary>
    public void RemoveField(string entity, string name)
        => (_working?["entities"]?[entity]?["fields"] as JsonObject)?.Remove(name);

    /// <summary>Sets or clears one operation's rule on an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The operation — one of list, get, create, update, delete.</param>
    /// <param name="cel">The CEL source, or empty to remove the rule.</param>
    public void SetRule(string entity, string operation, string cel)
    {
        if (_working?["entities"]?[entity] is not JsonObject declared)
        {
            return;
        }

        if (cel.Length == 0)
        {
            var existing = declared["rules"] as JsonObject;
            existing?.Remove(operation);
            if (existing is { Count: 0 })
            {
                declared.Remove("rules");
            }

            return;
        }

        Ensure(declared, "rules")[operation] = cel;
    }

    /// <summary>The fields an entity declares in the working copy, with their raw JSON.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FieldsOf(string entity)
        => _working?["entities"]?[entity]?["fields"] is JsonObject fields
            ? [.. fields.Select(pair => new KeyValuePair<string, string>(
                pair.Key, pair.Value?.ToJsonString() ?? "{}"))]
            : [];

    private static JsonObject Ensure(JsonNode? parent, string name)
    {
        if (parent is not JsonObject owner)
        {
            return [];
        }

        if (owner[name] is not JsonObject existing)
        {
            existing = [];
            owner[name] = existing;
        }

        return existing;
    }
}
