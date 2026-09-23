using MMLib.Alvo.Schema;

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

    /// <summary>
    /// Declares one index on an entity.
    /// </summary>
    /// <remarks>
    /// <b>Appended to the array the descriptor already has</b>, not written over it: the block is an ordered
    /// list and an author may have declared indexes this editor cannot draw. Replacing it would be the same
    /// silent narrowing the field editor refuses — an edit that drops what the control did not know about.
    /// </remarks>
    /// <param name="entity">The entity the index is on.</param>
    /// <param name="fields">The fields it covers, in the order they are declared in.</param>
    /// <param name="unique">Whether it enforces uniqueness across them.</param>
    public void AddIndex(string entity, IReadOnlyList<string> fields, bool unique)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (_working?["entities"]?[entity] is not JsonObject declared)
        {
            return;
        }

        if (declared["indexes"] is not JsonArray indexes)
        {
            indexes = [];
            declared["indexes"] = indexes;
        }

        var index = new JsonObject
        {
            ["fields"] = new JsonArray([.. fields.Select(field => JsonValue.Create(field))]),
        };

        /* Written only when true, for the reason the field editor writes its own booleans that way:
           `unique: false` is the schema's default, and a descriptor full of defaults is a descriptor
           whose diffs stop saying what changed. */
        if (unique)
        {
            index["unique"] = true;
        }

        indexes.Add(index);
    }

    /// <summary>
    /// Removes the index at one position.
    /// </summary>
    /// <remarks>
    /// By position rather than by the fields it covers, because the block is an array and the schema does
    /// not forbid two entries over the same fields — removing "the one on (a, b)" would then be ambiguous
    /// in exactly the case an operator is trying to clean up. The copy is per-circuit, so the position the
    /// screen rendered is the position this removes.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="position">The index's position in the declared array.</param>
    public void RemoveIndex(string entity, int position)
    {
        if (_working?["entities"]?[entity] is not JsonObject declared
            || declared["indexes"] is not JsonArray indexes
            || position < 0
            || position >= indexes.Count)
        {
            return;
        }

        indexes.RemoveAt(position);

        /* An empty array is not the same statement as no array, and the descriptor reads better without
           one — the same reason SetRule drops an emptied `rules`. */
        if (indexes.Count == 0)
        {
            declared.Remove("indexes");
        }
    }

    /// <summary>The indexes an entity declares in the working copy, in the order it declares them.</summary>
    /// <remarks>
    /// Read from the working document rather than from <c>SchemaModel</c> deliberately: the tab that edits
    /// them must render what it is editing, or an unapplied index disappears the moment it is added — which
    /// is the defect the Rules tab already records for its own case.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    public IReadOnlyList<IndexSchema> IndexesOf(string entity)
        => _working?["entities"]?[entity]?["indexes"] is JsonArray indexes
            ? [.. indexes.Select(Index)]
            : [];

    /// <summary>One declared index, in the shape every screen already reads.</summary>
    /// <param name="declared">The array entry.</param>
    private static IndexSchema Index(JsonNode? declared) => new(
        declared?["fields"] is JsonArray fields
            ? [.. fields.Select(field => field?.GetValue<string>() ?? string.Empty)]
            : [],
        declared?["unique"]?.GetValue<bool>() ?? false);

    /// <summary>
    /// Declares one hook at one point.
    /// </summary>
    /// <remarks>
    /// <b>Appended, like an index, and for the same reason</b>: a point's value is an ordered list and the
    /// order is the order they run in, so a writer that replaced it would both drop what it cannot draw and
    /// silently reorder what it can.
    /// </remarks>
    /// <param name="entity">The entity the hook is on.</param>
    /// <param name="point">The hook point — <c>beforeCreate</c> through <c>afterDelete</c>.</param>
    /// <param name="condition">The CEL guard, or empty for a hook that always runs.</param>
    /// <param name="action">The action, already in the schema's own shape.</param>
    public void AddHook(string entity, string point, string? condition, JsonObject action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_working?["entities"]?[entity] is not JsonObject declared)
        {
            return;
        }

        var hooks = Ensure(declared, "hooks");
        if (hooks[point] is not JsonArray list)
        {
            list = [];
            hooks[point] = list;
        }

        var hook = new JsonObject();

        /* Condition before action, because that is the order the schema lists them and the order a
           reader of the committed file wants: what guards this, then what it does. */
        if (!string.IsNullOrWhiteSpace(condition))
        {
            hook["condition"] = condition;
        }

        hook["action"] = action;
        list.Add(hook);
    }

    /// <summary>
    /// Removes the hook at one position of one point.
    /// </summary>
    /// <remarks>
    /// By position for <see cref="RemoveIndex"/>'s reason — nothing forbids two hooks with the same action —
    /// and an emptied point goes with it, then an emptied <c>hooks</c> block, so removing the only hook
    /// leaves the document an author would have written rather than two empty containers.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="position">The hook's position within that point.</param>
    public void RemoveHook(string entity, string point, int position)
    {
        if (_working?["entities"]?[entity] is not JsonObject declared
            || declared["hooks"] is not JsonObject hooks
            || hooks[point] is not JsonArray list
            || position < 0
            || position >= list.Count)
        {
            return;
        }

        list.RemoveAt(position);

        if (list.Count == 0)
        {
            hooks.Remove(point);
        }

        if (hooks.Count == 0)
        {
            declared.Remove("hooks");
        }
    }

    /// <summary>
    /// The hooks an entity declares in the working copy: hook point to the raw JSON of its list.
    /// </summary>
    /// <remarks>
    /// The same shape <c>DescriptorLens.Hooks</c> reads off the applied descriptor, so the tab renders one
    /// or the other without knowing which — and reads the working copy, because an editor whose additions
    /// do not appear until the apply is worse than no editor.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    public IReadOnlyList<KeyValuePair<string, string>> HooksOf(string entity)
        => _working?["entities"]?[entity]?["hooks"] is JsonObject hooks
            ? [.. hooks.Select(pair => new KeyValuePair<string, string>(
                pair.Key, pair.Value?.ToJsonString() ?? "[]"))]
            : [];

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
