using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Admin.Components.DesignSystem;
using MMLib.Alvo.Admin.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The entity's On write tab: the hooks it declares, and the one sheet that adds or edits one.</summary>
public partial class HooksTab
{
    private readonly HookBuilder _new = new();
    private readonly RefusalState<string> _refusal = new();
    private bool _adding;
    private PendingRemoval? _removing;
    private string? _reveal;
    private int _reveals;

    /// <summary>The hooks, as the descriptor declares them: point to the raw JSON of its list.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyList<KeyValuePair<string, string>> Hooks { get; set; } = [];

    /// <summary>The entity the hooks are on, for the expression check.</summary>
    [Parameter]
    public string Entity { get; set; } = string.Empty;

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

    /// <summary>The refusals that are about a hook, which are the only kind this tab can show.</summary>
    /// <remarks>
    /// The three action types this build never runs (<c>function</c>, <c>http.call</c>, <c>entity.update</c>), plus
    /// the two refused forms of an after-action's values (<c>JSONata</c>, <c>email.data</c>) — placed by
    /// <see cref="RefusalPlaces"/>, because a slot is the feature's own name and no prefix finds all five.
    /// </remarks>
    private IReadOnlyList<ManagementRefusedFeature> HookRefusals => RefusalPlaces.On(RefusalScreen.OnWrite, Refused);

    /// <summary>
    /// The working copy a typed expression is checked against, on a clone — and the one an edit is saved into (spec D3) —
    /// cascaded by the entity screen only, so its model stays internal.
    /// </summary>
    [CascadingParameter]
    private WorkingCopy? Copy { get; set; }

    private readonly ExpressionCheck _check = new();
    private readonly ComponentLifetime _lifetime = new();

    /// <summary>Redraws the boxes whenever a check has something new to show.</summary>
    public HooksTab() => _check.Changed += Redraw;

    /// <summary>The builder on screen: the hook being edited, or the next new one.</summary>
    private HookBuilder Current => _editing?.Builder ?? _new;

    private void Redraw() => _ = InvokeAsync(StateHasChanged);

    /// <inheritdoc />
    public void Dispose()
    {
        _check.Changed -= Redraw;
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Opens the sheet for a new hook, with the point and the kind the last one had.</summary>
    private void OpenNew()
    {
        _new.Fields = DeclaredFields();
        _editing = null;
        _refusal.Clear();
        _writable = WritableFields();
        EnsureMutateRow();
        _adding = true;
    }

    /// <summary>Switches the point, which may switch the kind (<see cref="HookBuilder.Choose"/>).</summary>
    private void Choose(string point)
    {
        Current.Choose(point);
        _refusal.Clear();
        CheckAll();
    }

    private void ChooseKind(string kind)
    {
        Current.Kind = kind;
        EnsureMutateRow();
        CheckAll();
    }

    /// <summary>The condition box is asked again, and only it.</summary>
    /// <remarks>
    /// A stated narrowing of the merged <c>CheckBoth()</c> (pre-flight C4): no action slot's candidate carries the condition
    /// (<see cref="HookBuilder.CandidateHook"/>, spec D4), so a condition edit cannot change their answer — and the guided
    /// condition (plan Task 19) calls this on every row change, where asking every box would be 3 + N checks per click.
    /// </remarks>
    private void TypeCondition(string? text)
    {
        Current.Condition = text ?? string.Empty;
        _ = CheckConditionAsync();
    }

    /// <summary>The point and the kind decide every slot's path, so every box on the form is asked again.</summary>
    private void CheckAll()
    {
        _ = CheckConditionAsync();
        CheckMutateValues();
    }

    /// <summary>The condition, checked in a hook that carries the draft's action (stand-ins for blanks).</summary>
    private Task CheckConditionAsync() => CheckAsync("hook-condition", Current.Condition, (copy, source)
        => ExpressionSlots.ForHook(
            copy.Json, Entity, Current.Point, CurrentPosition(copy), HookPatch.Apply(_editing?.Original, source, Current.Draft()), "condition"));

    /// <summary>
    /// Runs the check to its end and observes its fault: it is fire-and-forget, so an unobserved exception would
    /// otherwise vanish, and a helper that fails must never be the reason the form misbehaves.
    /// </summary>
    private async Task CheckAsync(string id, string text, Func<WorkingCopy, string, (string Json, string Path)?> place)
    {
        try
        {
            await _check.SubmitAsync(id, text, (source, ct) => AskAsync(place, source, ct));
        }
        catch (Exception ex)
        {
            CheckFailed(Logger, ex);
        }
    }

    /// <summary>Fixed text and the exception only: the entity name is the caller's string and is never logged raw.</summary>
    [LoggerMessage(EventId = 20, Level = LogLevel.Warning, Message = "The expression check on a hook input failed")]
    private static partial void CheckFailed(ILogger logger, Exception exception);

    private Task<ManagementExpressionVerdict?> AskAsync(
        Func<WorkingCopy, string, (string Json, string Path)?> place, string source, CancellationToken ct)
        => !_lifetime.Ended && Copy is { } copy && place(copy, source) is { } slot
            ? Gateway.CheckExpressionAsync(slot.Json, slot.Path, source, ct)
            : Task.FromResult<ManagementExpressionVerdict?>(null);

    /// <summary>
    /// The hooks declared at one point, each as its own JSON.
    /// </summary>
    /// <remarks>
    /// Parsed here rather than upstream so the tab keeps taking the one shape both the applied descriptor
    /// and the working copy are already read in. A point whose value is not an array renders as nothing
    /// rather than throwing: the descriptor reaching this screen has been applied, but the working copy may
    /// have been imported a moment ago and this tab is not the authority that refuses it.
    /// <para>
    /// <b>Each entry is written by <see cref="WorkingCopy.Readable"/>, never by options of the tab's own.</b> The text a row
    /// draws is what <see cref="WorkingCopy.ReplaceHook"/> compares against to refuse an edit made on a stale screen
    /// (spec §5.1): two separately written option sets that drifted apart would refuse every edit, silently.
    /// </para>
    /// </remarks>
    /// <param name="listJson">One point's list, as <see cref="WorkingCopy.HooksOf"/> reads it.</param>
    /// <returns>Each hook as its row draws it.</returns>
    internal static List<string> Declared(string listJson)
    {
        try
        {
            var parsed = JsonNode.Parse(listJson);
            return parsed is JsonArray list
                ? [.. list.Select(hook => WorkingCopy.Readable(hook, "{}"))]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Why the editor cannot draw this hook, or <see langword="null"/> when its row offers Edit.</summary>
    private static string? Undrawable(string point, string hookJson)
    {
        try
        {
            return HookShape.Undrawable(JsonNode.Parse(hookJson), point);
        }
        catch (JsonException)
        {
            return "It is not JSON the editor can read.";
        }
    }

    /// <summary>How many hooks a point declares now, for the edit sheet's subtitle.</summary>
    private int CountAt(string point) => Declared(Hooks.FirstOrDefault(declared => declared.Key == point).Value ?? "[]").Count;

    /// <summary>The primary action: adds the new hook, or saves the edited one.</summary>
    private async Task SubmitAsync()
    {
        if (_editing is { } editing)
        {
            SaveEdit(editing);
            return;
        }

        await AddAsync();
    }

    /// <summary>Declares the hook <see cref="HookBuilder.Build"/> makes, or shows why it cannot be made.</summary>
    private async Task AddAsync()
    {
        _new.Fields = DeclaredFields();
        if (_new.Build(out var refusal) is not { } action)
        {
            _refusal.Show(refusal);
            return;
        }

        /* The working copy appends to the point's list, so the new hook lands after the ones drawn there now. */
        var point = _new.Point;
        var position = CountAt(point);
        await OnAdd.InvokeAsync(new NewHook(point, _new.Condition, action));
        Reveal(point, position);
        CloseEditor();
    }

    /// <summary>Closes the sheet and forgets what was typed; a new hook's point and kind stay for the next one.</summary>
    private void CloseEditor()
    {
        _new.Clear();
        _editing = null;
        _adding = false;
        _refusal.Clear();
        CheckAll();
    }

    /// <summary>Lights the row at a place, and scrolls to it (spec §3.5).</summary>
    private void Reveal(string point, int position)
    {
        _reveal = RowId(point, position);
        _reveals++;
    }

    /// <summary>Whether the sheet holds anything closing would lose.</summary>
    /// <remarks>
    /// A new hook: anything typed — the point and the kind are not counted, because the builder keeps them from the last
    /// hook on purpose (<see cref="HookBuilder.Clear"/>), so counting them would ask "Discard your changes?" of a sheet the
    /// operator has not touched. An edit: anything that differs from what it opened with, the kind included.
    /// </remarks>
    private bool Dirty => _editing is { } editing ? Current.Fingerprint() != editing.Opened : _new.HasInput;

    /// <summary>The entity's fields as the working copy declares them now.</summary>
    private IReadOnlyDictionary<string, FieldSchema> DeclaredFields()
        => Copy is { } copy ? HookFields.Declared(copy.Json, Entity) : new Dictionary<string, FieldSchema>(StringComparer.Ordinal);

    /// <summary>
    /// The confirm's verb: asks the entity screen to drop the hook that was asked about, found again where it is now.
    /// </summary>
    /// <remarks>
    /// By what the hook is, not by where it was, for the Indexes tab's reason: another tab's edit reaching the copy
    /// while the confirm is up moves the rows. One no longer declared is left alone. Focus then goes to the row that
    /// took its place, or to the list's New hook (spec §3.2).
    /// </remarks>
    private async Task RemoveAsync()
    {
        if (_removing is not { } removing)
        {
            return;
        }

        _removing = null;
        var position = PositionIn(Hooks, removing.Point, removing.Json);
        if (position >= 0)
        {
            /* The rows after it move up, so the lit one would be a different hook. */
            _reveal = null;
            await OnRemove.InvokeAsync(new HookAt(removing.Point, position));
        }

        await Interop.FocusFirstOnceClosedAsync(
            [RemoveButton(removing.Point, position), RemoveButton(removing.Point, position - 1), "[data-testid='hook-new']"]);
    }

    /// <summary>Cancel and Escape keep the hook, and focus goes back to its Remove.</summary>
    private Task CancelRemoval()
    {
        var removing = _removing;
        _removing = null;
        return removing is null
            ? Task.CompletedTask
            : Interop.FocusFirstOnceClosedAsync(
                [RemoveButton(removing.Point, PositionIn(Hooks, removing.Point, removing.Json)), "[data-testid='hook-new']"]);
    }

    /// <summary>Where a hook, as drawn, is declared at its point in <paramref name="hooks"/>, or -1 when it no longer is.</summary>
    private static int PositionIn(IReadOnlyList<KeyValuePair<string, string>> hooks, string point, string json)
        => Declared(hooks.FirstOrDefault(declared => declared.Key == point).Value ?? "[]").IndexOf(json);

    private static string RemoveButton(string point, int position) => $"#{RowId(point, position)} [data-testid='hook-remove']";

    /// <summary>The hook the confirm is asking about: its point, and the declaration as it was drawn.</summary>
    /// <param name="Point">The hook point.</param>
    /// <param name="Json">The hook as the row drew it.</param>
    private sealed record PendingRemoval(string Point, string Json);

    /// <summary>One hook row's element id, by where it sits.</summary>
    private static string RowId(string point, int position) => $"hook-{point}-{position}";
}
