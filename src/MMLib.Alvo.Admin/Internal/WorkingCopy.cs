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
/// <para>
/// <b>One file per descriptor block, one primitive for all of them.</b> This file holds the documents, the
/// gate and <see cref="Edit"/>; <c>WorkingCopy.Entities.cs</c>, <c>.Fields.cs</c>, <c>.Indexes.cs</c>,
/// <c>.Hooks.cs</c> and <c>.Rules.cs</c> hold the edits of one block each. A block the dashboard learns next
/// (webhooks, templates, access, roles) is a new file whose edits go through <see cref="Edit"/>, and so
/// cannot forget the gate, the pending count or the event.
/// </para>
/// </remarks>
internal sealed partial class WorkingCopy
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
    private string? _suggestedReason;

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
    public string? SuggestedReason => Read(() => _suggestedReason);

    /// <summary>Records what an apply of this copy was asked for.</summary>
    /// <remarks>
    /// Under the gate like every other write: the assistant in one tab sets it while Preview in another may be
    /// discarding the copy, and a suggestion written after that discard would outlive the edits it described.
    /// </remarks>
    /// <param name="reason">The suggestion, rendered verbatim and parsed by nobody.</param>
    public void SuggestReason(string? reason)
    {
        lock (_gate)
        {
            _suggestedReason = reason;
        }
    }

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

    /// <summary>Takes a fresh working copy from an applied descriptor.</summary>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="revision">The revision it is at.</param>
    public void Take(string descriptorJson, int revision) => Edit(_ =>
    {
        Load(descriptorJson, revision);
        return true;
    });

    /// <summary>Takes a fresh working copy from an applied descriptor, unless one is already loaded.</summary>
    /// <remarks>
    /// <b>The question and the take are one step under the gate</b>, because the copy is shared by every tab
    /// the operator has open. Asked and taken separately, another tab could take the copy and stage an edit in
    /// between, and this take would then discard that edit — the loss the check exists to prevent.
    /// </remarks>
    /// <param name="descriptorJson">The descriptor as stored.</param>
    /// <param name="revision">The revision it is at.</param>
    /// <returns><see langword="true"/> when this call took it; <see langword="false"/> when it was already loaded.</returns>
    public bool TakeIfUnloaded(string descriptorJson, int revision) => Edit(_ =>
    {
        if (_working is not null)
        {
            return false;
        }

        Load(descriptorJson, revision);
        return true;
    });

    /// <summary>Discards every unapplied edit.</summary>
    public void Discard() => Edit(_ =>
    {
        _suggestedReason = null;
        _working = _applied is null ? null : JsonNode.Parse(_applied.ToJsonString());
        return true;
    });

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
        var parsed = false;
        Edit(_ =>
        {
            try
            {
                _working = JsonNode.Parse(descriptorJson);
            }
            catch (JsonException)
            {
                return false;
            }

            parsed = _working is not null;
            return true;
        });

        return parsed;
    }

    /// <summary>
    /// The one way anything writes to this copy: the gate, the record that something moved, and the
    /// announcement after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why one primitive.</b> Every edit used to spell out <c>try { lock { …; Touch(); } } finally { Settle(); }</c>
    /// for itself, fifteen times, and the next descriptor block would have been the sixteenth. An edit that forgot
    /// the lock races another tab, one that forgot <see cref="Settle"/> leaves the shell's count stale, and neither
    /// fails a test. Here the edit only says what it changes and whether it changed anything.
    /// </para>
    /// <para>
    /// <b>The change receives the working document's root</b>, or a detached empty object while nothing is loaded,
    /// so an edit before the first take lands nowhere — which is what writing into a missing parent always did.
    /// A change that replaces the document itself (a take, a discard, an import) ignores the argument.
    /// </para>
    /// </remarks>
    /// <param name="change">The edit, run under the gate; returns whether it changed anything.</param>
    /// <returns>What <paramref name="change"/> returned.</returns>
    private bool Edit(Func<JsonObject, bool> change)
    {
        bool changed;
        lock (_gate)
        {
            changed = change(_working as JsonObject ?? []);
            if (changed)
            {
                Touch();
            }
        }

        Settle();
        return changed;
    }

    /// <summary>Loads both documents from one applied descriptor. Under the gate.</summary>
    private void Load(string descriptorJson, int revision)
    {
        _applied = JsonNode.Parse(descriptorJson);
        _working = JsonNode.Parse(descriptorJson);
        Revision = revision;
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
