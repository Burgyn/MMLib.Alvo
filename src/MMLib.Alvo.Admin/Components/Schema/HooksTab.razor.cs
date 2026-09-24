using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The entity's On write tab: the hooks it declares, and the editor that adds one.</summary>
public partial class HooksTab
{
    /// <summary>
    /// How one hook is written for a person to read.
    /// </summary>
    /// <remarks>
    /// <b>Both options earn their place, and the encoder is the one that was missing.</b> A bare
    /// <c>ToJsonString()</c> takes the default encoder, which escapes anything that could be dangerous in
    /// HTML — so a CEL condition reached this tab as
    /// <c>old.status == &#92;u0027completed&#92;u0027 &#92;u0026&#92;u0026 …</c> and was rendered exactly like that.
    /// It is the defect <c>WorkingCopy</c> documents at length for the document as a whole, arriving one
    /// re-serialisation later. Safe because <c>CodeBlock</c> HTML-encodes before it highlights.
    /// Indented because one long line is what pushed this row wider than a phone.
    /// </remarks>
    private static readonly JsonSerializerOptions _readable = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HookBuilder _hook = new();
    private string? _refusal;
    private int _refusals;
    private bool _adding;
    private HookAt? _removing;
    private string? _reveal;
    private int _reveals;

    /// <summary>The hooks, as the descriptor declares them: point to the raw JSON of its list.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<KeyValuePair<string, string>> Hooks { get; set; } = [];

    /// <summary>Whether this operator has a working copy to edit at all.</summary>
    [Parameter]
    public bool Editable { get; set; }

    /// <summary>
    /// What the working copy changed, cascaded by the entity screen — a hook only it declares is badged
    /// <c>new</c>, because it does not run yet and must not read as if it did.
    /// </summary>
    [CascadingParameter]
    private StagedView Staged { get; set; } = StagedView.None;

    /// <summary>
    /// What this build refuses that belongs on this tab.
    /// </summary>
    /// <remarks>
    /// <b>Read from <c>ManagementCapabilities.Refused</c>, not listed in the markup.</b> Three of the five
    /// action types are declared by the schema and never run — and the tab said nothing about any of them,
    /// so the absence of a control for <c>entity.update</c> read as an oversight rather than a refusal.
    /// <c>FieldEditor</c> already does exactly this for its own half; this is the entity half the audit
    /// found nobody consuming.
    /// </remarks>
    [Parameter]
    public IReadOnlyList<ManagementRefusedFeature> Refused { get; set; } = [];

    /// <summary>Raised with the hook to declare.</summary>
    [Parameter]
    public EventCallback<NewHook> OnAdd { get; set; }

    /// <summary>Raised with the hook to remove.</summary>
    [Parameter]
    public EventCallback<HookAt> OnRemove { get; set; }

    /// <summary>A hook this editor built, in the schema's own shape.</summary>
    /// <param name="Point">The hook point it belongs to.</param>
    /// <param name="Condition">The CEL guard, or empty for a hook that always runs.</param>
    /// <param name="Action">The action object.</param>
    public sealed record NewHook(string Point, string? Condition, JsonObject Action);

    /// <summary>One declared hook, by where it sits.</summary>
    /// <param name="Point">The hook point.</param>
    /// <param name="Position">Its position within that point's list.</param>
    public sealed record HookAt(string Point, int Position);

    /// <summary>The refusals that are about an action type, which is the only kind this tab can show.</summary>
    /// <remarks>
    /// Matched against the three discriminators rather than a prefix, because a refusal's slot is the
    /// feature's own name for an action — <c>entity.update</c>, not <c>action.entity.update</c>.
    /// </remarks>
    private IReadOnlyList<ManagementRefusedFeature> ActionRefusals =>
        [.. Refused.Where(refusal =>
            refusal.Slot is "function" or "http.call" or "entity.update")];

    /// <summary>Switches the point, which may switch the kind (<see cref="HookBuilder.Choose"/>).</summary>
    private void Choose(string point)
    {
        _hook.Choose(point);
        _refusal = null;
    }

    /// <summary>
    /// The hooks declared at one point, each as its own JSON.
    /// </summary>
    /// <remarks>
    /// Parsed here rather than upstream so the tab keeps taking the one shape both the applied descriptor
    /// and the working copy are already read in. A point whose value is not an array renders as nothing
    /// rather than throwing: the descriptor reaching this screen has been applied, but the working copy may
    /// have been imported a moment ago and this tab is not the authority that refuses it.
    /// </remarks>
    private static List<string> Declared(string listJson)
    {
        try
        {
            var parsed = JsonNode.Parse(listJson);
            return parsed is JsonArray list
                ? [.. list.Select(hook => hook?.ToJsonString(_readable) ?? "{}")]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Declares the hook <see cref="HookBuilder.Build"/> makes, or shows why it cannot be made.</summary>
    private async Task AddAsync()
    {
        if (_hook.Build(out _refusal) is not { } action)
        {
            _refusals++;
            return;
        }

        /* The working copy appends to the point's list, so the new hook lands after the ones drawn there now. */
        var point = _hook.Point;
        var position = Declared(Hooks.FirstOrDefault(declared => declared.Key == point).Value ?? "[]").Count;
        await OnAdd.InvokeAsync(new NewHook(point, _hook.Condition, action));
        _reveal = RowId(point, position);
        _reveals++;
        CloseAdding();
    }

    /// <summary>Closes the editor and forgets what was typed; the point and the kind stay for the next hook.</summary>
    private void CloseAdding()
    {
        _hook.Clear();
        _refusal = null;
        _adding = false;
    }

    /// <summary>Whether the editor holds anything typed, which closing would lose.</summary>
    /// <remarks>
    /// The point and the kind are not counted. They are two clicks to choose again, and the builder keeps them from
    /// the last hook on purpose (<see cref="HookBuilder.Clear"/>), so counting them would ask "Discard your changes?"
    /// of an editor the operator has not touched since it opened. A stated deviation from the brief's
    /// <c>Point is not null || Kind is not null</c>, which is always true.
    /// </remarks>
    private bool Dirty => _hook.Condition.Length > 0 || _hook.RejectMessage.Length > 0 || _hook.MutateField.Length > 0
        || _hook.MutateValue.Length > 0 || _hook.Endpoint.Length > 0 || _hook.Template.Length > 0 || _hook.To.Length > 0;

    /// <summary>The confirm's verb: asks the entity screen to drop the hook that was asked about.</summary>
    private async Task RemoveAsync()
    {
        if (_removing is { } at)
        {
            _removing = null;
            /* The rows after it move up, so the lit one would be a different hook. */
            _reveal = null;
            await OnRemove.InvokeAsync(at);
        }
    }

    /// <summary>One hook row's element id, by where it sits.</summary>
    private static string RowId(string point, int position) => $"hook-{point}-{position}";
}
