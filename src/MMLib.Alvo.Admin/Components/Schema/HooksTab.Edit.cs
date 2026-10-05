using MMLib.Alvo.Admin.Internal;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Admin.Components.Schema;

/* Editing a declared hook where it sits (spec §4.2, §5.1; ruling B1). */
public partial class HooksTab
{
    /// <summary>The in-place refusal when the hook changed under the open sheet.</summary>
    private const string ChangedElsewhere =
        "This hook changed in the working copy after you opened it — in another tab, by the assistant or by an import. "
        + "Nothing was saved. Close this editor and open the hook again.";

    private Editing? _editing;

    /// <summary>Opens the sheet on a declared hook, loaded whole; a hook the editor cannot draw has no Edit to press.</summary>
    /// <param name="point">The point it is declared at.</param>
    /// <param name="position">Where its row is.</param>
    /// <param name="json">The hook as its row drew it.</param>
    private void OpenEdit(string point, int position, string json)
    {
        if (JsonNode.Parse(json) is not JsonObject original
            || HookBuilder.From(point, original, DeclaredFields()) is not { } builder)
        {
            return;
        }

        _adding = false;
        _editing = new Editing(point, position, json, original, builder, builder.Fingerprint());
        _writable = WritableFields();
        ReadPickers();
        _refusal.Clear();
        CheckAll();
    }

    /// <summary>
    /// Writes the edit onto the hook it opened — found again by what it was, then replaced under the copy's gate only if it
    /// is still exactly that (spec §5.1). Otherwise the sheet stays open with the refusal in place.
    /// </summary>
    private void SaveEdit(Editing editing)
    {
        editing.Builder.Fields = DeclaredFields();
        if (editing.Builder.BuildHook(editing.Original, out var refusal) is not { } hook)
        {
            _refusal.Show(refusal);
            return;
        }

        var position = Copy is { } copy ? PositionIn(copy.HooksOf(Entity), editing.Point, editing.Json) : -1;
        if (position < 0 || !Copy!.ReplaceHook(Entity, editing.Point, position, editing.Json, hook))
        {
            _refusal.Show(ChangedElsewhere);
            return;
        }

        Snackbar.Confirm(StagedWords.Saved("Hook", editing.Point));
        Reveal(editing.Point, position);
        CloseEditor();
    }

    /// <summary>Where the edited hook sits in the copy now — or <see langword="null"/> for a new hook, which is appended.</summary>
    private int? CurrentPosition(WorkingCopy copy)
        => _editing is { } editing ? PositionIn(copy.HooksOf(Entity), editing.Point, editing.Json) : null;

    /// <summary>
    /// Where an action box's text is checked: the candidate hook — the draft patched onto the opened one, without the
    /// condition (spec D4) — placed at the edited hook's position, or appended for a new one. The one place every action
    /// slot asks (pre-flight D4).
    /// </summary>
    /// <param name="copy">The working copy the check runs against.</param>
    /// <param name="slot">The slot's path inside the hook.</param>
    private (string Json, string Path)? ActionSlot(WorkingCopy copy, params string[] slot)
        => ExpressionSlots.ForHook(copy.Json, Entity, Current.Point, CurrentPosition(copy), Current.CandidateHook(_editing?.Original), slot);

    /// <summary>The condition box's descriptions: its hint, the length sentence while there is one, and the check's.</summary>
    private string ConditionDescribedBy
        => Current.Condition.Length > ConditionTable.MaxConditionLength
            ? $"{Described("hook-condition")} hook-condition-length"
            : Described("hook-condition");

    /// <summary>The hook the sheet opened.</summary>
    /// <param name="Point">The point it is declared at.</param>
    /// <param name="Position">Where its row was when it opened.</param>
    /// <param name="Json">The hook as its row drew it — what the save finds it by.</param>
    /// <param name="Original">The hook as declared, which the edit is patched onto.</param>
    /// <param name="Builder">What the sheet holds.</param>
    /// <param name="Opened">The builder's fingerprint when it opened.</param>
    private sealed record Editing(string Point, int Position, string Json, JsonObject Original, HookBuilder Builder, string Opened);
}
