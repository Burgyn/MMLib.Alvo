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
/// <b>Scoped to the operator, not the circuit</b> (see <see cref="WorkingCopyStore"/>). An operator's
/// unapplied edits are theirs, shared by every tab they have open; another operator's apply moves the
/// revision, and this copy's next apply is then refused by <c>If-Match</c> rather than overwriting them.
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

    /// <summary>
    /// One fragment of the working document, as a screen should read it.
    /// </summary>
    /// <remarks>
    /// <b><see cref="_pretty"/>, never a bare <c>ToJsonString()</c>, and the reason is one line up.</b> The
    /// default encoder escapes anything that could be dangerous in HTML, so a hook's CEL condition reached
    /// the On write tab as <c>old.status == \u0027completed\u0027 \u0026\u0026 …</c> and was rendered to
    /// operator exactly like that. Unreadable rather than wrong, which is the worse kind of display bug:
    /// nothing fails, and the screen quietly stops being about the descriptor the author wrote. Safe here
    /// because <c>CodeBlock</c> HTML-encodes before it highlights.
    /// </remarks>
    /// <param name="node">The fragment, or <see langword="null"/>.</param>
    /// <param name="empty">What an absent fragment reads as.</param>
    private static string Readable(JsonNode? node, string empty) =>
        node?.ToJsonString(_pretty) ?? empty;

    /// <summary>
    /// Serialises every read and write of the two documents.
    /// </summary>
    /// <remarks>
    /// The copy belongs to the operator, not to a circuit, so two tabs are two threads over one pair of
    /// <see cref="JsonNode"/> trees — and <see cref="Changed"/> makes a read in one straight after a write in
    /// the other the ordinary case rather than a coincidence. <c>JsonNode</c> is not safe for that.
    /// </remarks>
    private readonly Lock _gate = new();

    private JsonNode? _applied;
    private JsonNode? _working;

    /// <summary>The pending count as of the last edit, so the shell reads an int rather than two serialisations.</summary>
    private int _pending;

    /// <summary>Whether an edit under the gate changed anything <see cref="Settle"/> has not yet announced.</summary>
    private bool _touched;

    /// <summary>The revision the working copy was taken from.</summary>
    public int Revision { get; private set; }

    /// <summary>Whether a working copy has been loaded at all.</summary>
    public bool Loaded => _working is not null;

    /// <summary>The working document, formatted.</summary>
    public string Json => Read(() => _working?.ToJsonString(_pretty) ?? "{}");

    /// <summary>The applied document, formatted.</summary>
    public string AppliedJson => Read(() => _applied?.ToJsonString(_pretty) ?? "{}");

    /// <summary>
    /// Raised after anything changed this copy — an edit, an import, a discard, a fresh take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It exists for the shell, which shows the pending count on every screen and edits none of
    /// them.</b> The page that staged a field knows it did; the bar under the header and the project card
    /// do not, and without this they would say "nothing to apply" until the next navigation.
    /// </para>
    /// <para>
    /// <b>It can be raised on another circuit's thread.</b> The copy is the operator's, not the circuit's
    /// (see <see cref="WorkingCopyStore"/>), so an edit in one tab raises it in every tab they have open —
    /// a handler redraws through <c>InvokeAsync</c>, never directly.
    /// </para>
    /// </remarks>
    public event Action? Changed;

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

    /// <summary>Whether the working document differs from the applied one.</summary>
    public bool IsDirty => Read(() => !string.Equals(Json, AppliedJson, StringComparison.Ordinal));

    /// <summary>
    /// How many changes are waiting for an apply, as an operator would count them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never zero while the copy is dirty: a document that differs only in the order of its entities is
    /// still one the preview shows a diff for, and a bar reading "0 unapplied changes" above a Preview
    /// button contradicts itself.
    /// </para>
    /// <para>
    /// Taken once per edit, under the gate, rather than on every read: the bar and the project card both
    /// render it on every render of the shell, and each read would otherwise serialise both documents.
    /// </para>
    /// </remarks>
    public int PendingCount => Volatile.Read(ref _pending);

    /// <summary>The entity names the working document declares.</summary>
    public IReadOnlyList<string> Entities => Read<IReadOnlyList<string>>(
        () => _working?["entities"] is JsonObject entities ? [.. entities.Select(entity => entity.Key)] : []);

    /// <summary>Takes a fresh working copy from an applied descriptor.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="revision">The revision it is at.</param>
    public void Take(string descriptorJson, int revision)
    {
        try
        {
            lock (_gate)
            {
                _applied = JsonNode.Parse(descriptorJson);
                _working = JsonNode.Parse(descriptorJson);
                Revision = revision;
                Touch();
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>Discards every unapplied edit.</summary>
    public void Discard()
    {
        try
        {
            lock (_gate)
            {
                SuggestedReason = null;
                _working = _applied is null ? null : JsonNode.Parse(_applied.ToJsonString());
                Touch();
            }
        }
        finally
        {
            Settle();
        }
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
            lock (_gate)
            {
                try
                {
                    _working = JsonNode.Parse(descriptorJson);
                    Touch();
                    return _working is not null;
                }
                catch (JsonException)
                {
                    return false;
                }
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>Adds an entity with one required string field, the smallest thing the schema admits.</summary>
    /// <param name="name">The entity's name.</param>
    /// <param name="scoped">Whether it is tenant-scoped.</param>
    /// <param name="audited">Whether it carries the audit columns.</param>
    public void AddEntity(string name, bool scoped, bool audited)
    {
        try
        {
            lock (_gate)
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
                Touch();
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>
    /// Renames an entity, carrying the rename so the apply moves the table instead of dropping it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key moves and <c>renamedFrom</c> records where it came from</b>, because those are one
    /// operation and not two. Renaming the key alone is a drop and a create to the apply — the old table
    /// goes, with its rows — which is the data loss the schema has this key to prevent.
    /// </para>
    /// <para>
    /// <b><c>renamedFrom</c> always names the APPLIED name, never an intermediate.</b> An operator who
    /// renames <c>jobs</c> to <c>work_orders</c> and then to <c>orders</c> has one applied table called
    /// <c>jobs</c>, so that is what the apply has to be told; recording <c>work_orders</c> would point it at
    /// a table that never existed. And a rename back to the applied name drops the key entirely, because
    /// then nothing moved.
    /// </para>
    /// <para>
    /// <b>An entity this copy invented carries no <c>renamedFrom</c> at all.</b> It has no table behind it,
    /// so there is nothing to move and the key would name something the apply cannot find.
    /// </para>
    /// </remarks>
    /// <param name="from">The name it has now.</param>
    /// <param name="to">The name it should have.</param>
    /// <returns>A sentence saying why it cannot, or <see langword="null"/> when it was renamed.</returns>
    public string? RenameEntity(string from, string to)
    {
        try
        {
            lock (_gate)
            {
                if (_working?["entities"] is not JsonObject entities || entities[from] is not JsonObject declared)
                {
                    return $"There is no entity called {from} in this working copy.";
                }

                if (Refusal(from, to, entities, "entity") is { } refusal)
                {
                    return refusal;
                }

                var origin = Origin(declared, from, _applied?["entities"]?[from] is not null);
                Rekey(entities, from, to);
                Carry(entities[to] as JsonObject, origin, to);
                Touch();

                return null;
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>
    /// Renames a field, carrying the rename so the apply moves the column instead of dropping it.
    /// </summary>
    /// <remarks>
    /// <see cref="RenameEntity"/>'s rules, one level down — including that the origin is read against the
    /// applied document, so a field on an entity this copy invented carries no <c>renamedFrom</c> either.
    /// </remarks>
    /// <param name="entity">The entity the field is on.</param>
    /// <param name="from">The name it has now.</param>
    /// <param name="to">The name it should have.</param>
    /// <returns>A sentence saying why it cannot, or <see langword="null"/> when it was renamed.</returns>
    public string? RenameField(string entity, string from, string to)
    {
        try
        {
            lock (_gate)
            {
                if (_working?["entities"]?[entity]?["fields"] is not JsonObject fields
                    || fields[from] is not JsonObject declared)
                {
                    return $"There is no field called {from} on {entity}.";
                }

                if (Refusal(from, to, fields, "field") is { } refusal)
                {
                    return refusal;
                }

                /* Against the entity's applied name, not its working one: renaming a field on an entity that was
                   itself renamed in this copy must still resolve the column that exists in the database. */
                var appliedEntity = AppliedNameOf(entity);
                var origin = Origin(
                    declared, from, _applied?["entities"]?[appliedEntity]?["fields"]?[from] is not null);

                Rekey(fields, from, to);
                Carry(fields[to] as JsonObject, origin, to);
                Touch();

                return null;
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>The name this entity is applied under, which is what a field's origin has to be read against.</summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    private string AppliedNameOf(string entity) =>
        _working?["entities"]?[entity]?["renamedFrom"]?.GetValue<string>() ?? entity;

    /// <summary>
    /// The applied name this declaration came from, or <see langword="null"/> when it never had one.
    /// </summary>
    /// <param name="declared">The declaration being renamed.</param>
    /// <param name="from">The name it carries in the working copy right now.</param>
    /// <param name="isApplied">Whether <paramref name="from"/> is a name the applied document knows.</param>
    private static string? Origin(JsonObject declared, string from, bool isApplied)
        => declared["renamedFrom"] is JsonValue existing && existing.TryGetValue<string>(out var origin)
            ? origin
            : isApplied ? from : null;

    /// <summary>Writes, or drops, the rename the apply reads.</summary>
    /// <param name="declared">The renamed declaration.</param>
    /// <param name="origin">The applied name it came from, or <see langword="null"/>.</param>
    /// <param name="to">The name it now has.</param>
    private static void Carry(JsonObject? declared, string? origin, string to)
    {
        if (declared is null)
        {
            return;
        }

        if (origin is null || string.Equals(origin, to, StringComparison.Ordinal))
        {
            declared.Remove("renamedFrom");
            return;
        }

        declared["renamedFrom"] = origin;
    }

    /// <summary>Why this rename cannot happen, or <see langword="null"/>.</summary>
    /// <param name="from">The current name.</param>
    /// <param name="to">The proposed name.</param>
    /// <param name="owner">The object the name is a key of.</param>
    /// <param name="what">The word for what is being renamed, for the sentence.</param>
    private static string? Refusal(string from, string to, JsonObject owner, string what)
    {
        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            return "That is the name it already has.";
        }

        if (!DescriptorNames.IsMember(to))
        {
            return $"A {what} name must match {DescriptorNames.Member}.";
        }

        return owner[to] is not null
            ? $"This project already declares a {what} called {to}."
            : null;
    }

    /// <summary>
    /// Moves a key without moving its position.
    /// </summary>
    /// <remarks>
    /// Removing and re-adding would send the renamed declaration to the end of the object, so a rename
    /// would land in the diff as the whole block moving. The descriptor is a file somebody reads and
    /// commits, and a one-word change should read as one word.
    /// </remarks>
    /// <param name="owner">The object to rewrite.</param>
    /// <param name="from">The key to replace.</param>
    /// <param name="to">The key to replace it with.</param>
    private static void Rekey(JsonObject owner, string from, string to)
    {
        var order = owner
            .Select(pair => (
                Key: string.Equals(pair.Key, from, StringComparison.Ordinal) ? to : pair.Key,
                Value: pair.Value?.DeepClone()))
            .ToList();

        owner.Clear();
        foreach (var (key, value) in order)
        {
            owner[key] = value;
        }
    }

    /// <summary>Removes an entity.</summary>
    public void RemoveEntity(string name)
    {
        try
        {
            lock (_gate)
            {
                if ((_working?["entities"] as JsonObject)?.Remove(name) is true)
                {
                    Touch();
                }
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>Adds a field to an entity.</summary>
    /// <param name="entity">The entity to add it to.</param>
    /// <param name="name">The field's name.</param>
    /// <param name="facets">Its type and facets, already in the schema's own shape.</param>
    public void AddField(string entity, string name, JsonObject facets)
    {
        try
        {
            lock (_gate)
            {
                if (_working?["entities"]?[entity] is JsonObject declared)
                {
                    var fields = Ensure(declared, "fields");
                    fields[name] = facets;
                    Touch();
                }
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>Removes a field from an entity.</summary>
    public void RemoveField(string entity, string name)
    {
        try
        {
            lock (_gate)
            {
                if ((_working?["entities"]?[entity]?["fields"] as JsonObject)?.Remove(name) is true)
                {
                    Touch();
                }
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>Sets or clears one operation's rule on an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The operation — one of list, get, create, update, delete.</param>
    /// <param name="cel">The CEL source, or empty to remove the rule.</param>
    public void SetRule(string entity, string operation, string cel)
    {
        try
        {
            lock (_gate)
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

                    Touch();
                    return;
                }

                Ensure(declared, "rules")[operation] = cel;
                Touch();
            }
        }
        finally
        {
            Settle();
        }
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
        try
        {
            lock (_gate)
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
                Touch();
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>
    /// Removes the index at one position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// By position rather than by the fields it covers, because the block is an array and the schema does
    /// not forbid two entries over the same fields — removing "the one on (a, b)" would then be ambiguous
    /// in exactly the case an operator is trying to clean up.
    /// </para>
    /// <para>
    /// <b>A position is only as good as the render it came from.</b> The copy is the operator's, shared by
    /// every tab they have open, so another tab can have moved the array since this screen drew it — and a
    /// bare position would then remove somebody else's index. <paramref name="expected"/> is what the screen
    /// drew there; when the entry no longer matches, nothing is removed and the screen redraws from the copy.
    /// </para>
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="position">The index's position in the declared array.</param>
    /// <param name="expected">The index the screen rendered at that position, or <see langword="null"/> to skip the check.</param>
    /// <returns><see langword="true"/> when an index was removed.</returns>
    public bool RemoveIndex(string entity, int position, IndexSchema? expected = null)
    {
        var removed = false;
        try
        {
            lock (_gate)
            {
                if (_working?["entities"]?[entity] is not JsonObject declared
                    || declared["indexes"] is not JsonArray indexes
                    || position < 0
                    || position >= indexes.Count
                    || (expected is not null && !SameIndex(Index(indexes[position]), expected)))
                {
                    return false;
                }

                indexes.RemoveAt(position);
                removed = true;

                /* An empty array is not the same statement as no array, and the descriptor reads better without
                   one — the same reason SetRule drops an emptied `rules`. */
                if (indexes.Count == 0)
                {
                    declared.Remove("indexes");
                }

                Touch();
            }
        }
        finally
        {
            Settle();
        }

        return removed;
    }

    /// <summary>Whether two readings of an index declare the same one.</summary>
    private static bool SameIndex(IndexSchema actual, IndexSchema expected)
        => actual.Unique == expected.Unique && actual.Fields.SequenceEqual(expected.Fields, StringComparer.Ordinal);

    /// <summary>The indexes an entity declares in the working copy, in the order it declares them.</summary>
    /// <remarks>
    /// Read from the working document rather than from <c>SchemaModel</c> deliberately: the tab that edits
    /// them must render what it is editing, or an unapplied index disappears the moment it is added — which
    /// is the defect the Rules tab already records for its own case.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    public IReadOnlyList<IndexSchema> IndexesOf(string entity) => Read<IReadOnlyList<IndexSchema>>(
        () => _working?["entities"]?[entity]?["indexes"] is JsonArray indexes
            ? [.. indexes.Select(Index)]
            : []);

    /// <summary>
    /// One declared index, in the shape every screen already reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It reads defensively, and that is not belt-and-braces.</b> <see cref="Replace"/> validates nothing
    /// beyond "is it JSON" on purpose — the apply is the authority — so an imported descriptor or an
    /// assistant's proposal can put <c>"unique": "yes"</c> or a number among the field names into the working
    /// copy. <c>JsonNode.GetValue&lt;T&gt;</c> throws <see cref="InvalidOperationException"/> for that, and
    /// <c>Entity.razor</c> reads the indexes on every tab, so one bad entry ended the circuit instead of
    /// spoiling one panel. <c>HooksTab</c> already degraded this way and this path did not.
    /// </para>
    /// <para>
    /// <b>An unreadable entry is kept, not skipped</b>, because <see cref="RemoveIndex"/> addresses the array
    /// by position: dropping it here would shift every position after it and remove the wrong index. It comes
    /// back with no fields, which the tab renders as the one thing an operator wants for it — a row they can
    /// delete.
    /// </para>
    /// </remarks>
    /// <param name="declared">The array entry.</param>
    private static IndexSchema Index(JsonNode? declared)
    {
        if (declared is not JsonObject index)
        {
            return new([], Unique: false);
        }

        return new(
            index["fields"] is JsonArray fields ? [.. fields.Select(Name).OfType<string>()] : [],
            index["unique"] is JsonValue unique && unique.TryGetValue<bool>(out var enforced) && enforced);
    }

    /// <summary>One field name, or nothing when the entry is not one.</summary>
    private static string? Name(JsonNode? field) =>
        field is JsonValue value && value.TryGetValue<string>(out var name) ? name : null;

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
        try
        {
            lock (_gate)
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
                Touch();
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>
    /// Removes the hook at one position of one point.
    /// </summary>
    /// <remarks>
    /// By position for <see cref="RemoveIndex"/>'s reason — nothing forbids two hooks with the same action —
    /// and an emptied point goes with it, then an emptied <c>hooks</c> block, so removing the only hook
    /// leaves the document an author would have written rather than two empty containers. Checked against
    /// what the screen rendered, for <see cref="RemoveIndex"/>'s reason: another tab may have moved the list.
    /// </remarks>
    /// <param name="entity">The entity.</param>
    /// <param name="point">The hook point.</param>
    /// <param name="position">The hook's position within that point.</param>
    /// <param name="expectedList">
    /// The point's list as the screen rendered it (<see cref="HooksOf"/>'s shape), or <see langword="null"/> to
    /// skip the check. The whole list rather than one entry, because a hook is only addressable by its place in it.
    /// </param>
    /// <returns><see langword="true"/> when a hook was removed.</returns>
    public bool RemoveHook(string entity, string point, int position, string? expectedList = null)
    {
        var removed = false;
        try
        {
            lock (_gate)
            {
                if (_working?["entities"]?[entity] is not JsonObject declared
                    || declared["hooks"] is not JsonObject hooks
                    || hooks[point] is not JsonArray list
                    || position < 0
                    || position >= list.Count
                    || (expectedList is not null
                        && !string.Equals(Readable(list, "[]"), expectedList, StringComparison.Ordinal)))
                {
                    return false;
                }

                list.RemoveAt(position);
                removed = true;

                if (list.Count == 0)
                {
                    hooks.Remove(point);
                }

                if (hooks.Count == 0)
                {
                    declared.Remove("hooks");
                }

                Touch();
            }
        }
        finally
        {
            Settle();
        }

        return removed;
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
        => Read<IReadOnlyList<KeyValuePair<string, string>>>(
            () => _working?["entities"]?[entity]?["hooks"] is JsonObject hooks
                ? [.. hooks.Select(pair => new KeyValuePair<string, string>(
                    pair.Key, Readable(pair.Value, "[]")))]
                : []);

    /// <summary>The fields an entity declares in the working copy, with their raw JSON.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FieldsOf(string entity)
        => Read<IReadOnlyList<KeyValuePair<string, string>>>(
            () => _working?["entities"]?[entity]?["fields"] is JsonObject fields
                ? [.. fields.Select(pair => new KeyValuePair<string, string>(
                    pair.Key, Readable(pair.Value, "{}")))]
                : []);

    /// <summary>
    /// An entity's fields as the Fields tab lists them: the working copy's, and the ones it removed.
    /// </summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    public IReadOnlyList<StagedField> FieldChangesOf(string entity)
        => Read(() => StagedChanges.Fields(AppliedEntityOf(entity), WorkingEntityOf(entity)));

    /// <summary>The positions of the indexes this copy declared and the applied revision does not.</summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    public IReadOnlySet<int> StagedIndexesOf(string entity)
        => Read(() => StagedChanges.NewEntries(
            AppliedEntityOf(entity)?["indexes"] as JsonArray, WorkingEntityOf(entity)?["indexes"] as JsonArray));

    /// <summary>The hooks, by point and position, this copy declared and the applied revision does not.</summary>
    /// <param name="entity">The entity's name in the working copy.</param>
    public IReadOnlySet<(string Point, int Position)> StagedHooksOf(string entity) => Read(() => HooksStagedOn(entity));

    private HashSet<(string Point, int Position)> HooksStagedOn(string entity)
    {
        var applied = AppliedEntityOf(entity)?["hooks"] as JsonObject;
        var working = WorkingEntityOf(entity)?["hooks"] as JsonObject ?? [];

        return working
            .SelectMany(point => StagedChanges
                .NewEntries(applied?[point.Key] as JsonArray, point.Value as JsonArray)
                .Select(position => (point.Key, position)))
            .ToHashSet();
    }

    /// <summary>
    /// Puts a removed field back, exactly as the applied revision declares it.
    /// </summary>
    /// <remarks>
    /// <b>Back in its place, not at the end.</b> <see cref="IsDirty"/> compares the documents as text, so a
    /// field restored to the end of the object would leave the copy dirty with nothing an operator could
    /// name as a change — undoing the only edit must leave nothing to preview.
    /// </remarks>
    /// <param name="entity">The entity's name in the working copy.</param>
    /// <param name="name">The field's applied name.</param>
    public void RestoreField(string entity, string name)
    {
        try
        {
            lock (_gate)
            {
                if (AppliedEntityOf(entity)?["fields"] is not JsonObject applied
                    || applied[name] is not { } declared
                    || WorkingEntityOf(entity) is not { } working)
                {
                    return;
                }

                var fields = Ensure(working, "fields");
                if (fields.ContainsKey(name))
                {
                    return;
                }

                Reinsert(fields, name, declared.DeepClone(), applied);
                Touch();
            }
        }
        finally
        {
            Settle();
        }
    }

    /// <summary>Inserts a key just after the last working key whose applied name preceded it.</summary>
    /// <remarks>
    /// Rebuilt rather than appended, for the reason <see cref="Rekey"/> gives. A working key is read through
    /// its <c>renamedFrom</c>, as <c>StagedChanges</c> places a removed row: a field renamed since it was
    /// applied still stands where its applied name stood.
    /// </remarks>
    private static void Reinsert(JsonObject owner, string name, JsonNode value, JsonObject applied)
    {
        var order = applied.Select(pair => pair.Key).ToList();
        var earlier = order.Take(order.IndexOf(name)).ToHashSet(StringComparer.Ordinal);
        var pairs = owner.Select(pair => (pair.Key, Value: pair.Value?.DeepClone())).ToList();
        var at = pairs.FindLastIndex(
            pair => StagedChanges.OriginOf(pair.Value, pair.Key, applied) is { } origin && earlier.Contains(origin)) + 1;

        pairs.Insert(at, (name, value));
        owner.Clear();
        foreach (var (key, node) in pairs)
        {
            owner[key] = node;
        }
    }

    /// <summary>The entity as the working copy declares it.</summary>
    private JsonObject? WorkingEntityOf(string entity)
        => (_working as JsonObject)?["entities"] is JsonObject entities ? entities[entity] as JsonObject : null;

    /// <summary>The entity as applied — under its own name, or under the name it was renamed from.</summary>
    private JsonObject? AppliedEntityOf(string entity)
    {
        var applied = (_applied as JsonObject)?["entities"] as JsonObject;
        var origin = StagedChanges.OriginOf(WorkingEntityOf(entity), entity, applied);

        return origin is null ? null : applied![origin] as JsonObject;
    }

    /// <summary>Records, under the gate, that this edit changed something.</summary>
    private void Touch() => _touched = true;

    /// <summary>
    /// After an edit: takes the pending count and tells whoever shows this copy that it moved.
    /// </summary>
    /// <remarks>
    /// The event is raised outside the gate, because a handler belongs to another component — possibly on
    /// another circuit — and holding a lock while calling code nobody here wrote is how a lock deadlocks.
    /// </remarks>
    private void Settle()
    {
        bool changed;
        lock (_gate)
        {
            changed = _touched;
            _touched = false;
            if (changed)
            {
                Volatile.Write(ref _pending, IsDirty ? Math.Max(1, StagedChanges.Count(_applied, _working)) : 0);
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reads under the gate.</summary>
    private T Read<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

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
