using MMLib.Alvo.Schema;
using InputMode = MudBlazor.InputMode;

namespace MMLib.Alvo.Admin.Components.Schema;

/* A mutate's rows: several fields, each a value its type holds or a CEL expression (spec §4.3, ruling B3). */
public partial class HooksTab
{
    private const string ValueWord = "a value";
    private const string ExpressionWord = "an expression";
    private static readonly string[] _mutateModes = [ValueWord, ExpressionWord];
    private static readonly string[] _flags = ["true", "false"];

    /// <summary>The fields a mutate may name, read when the sheet opens (<see cref="HookFields.Writable"/>).</summary>
    private IReadOnlyList<string> _writable = [];

    /// <summary>How many row check keys may still hold a sentence: every key past the last row is cleared once.</summary>
    private int _mutateKeys;

    private void AddMutateRow()
    {
        Current.MutateRows.Add(new MutateRow());
        CheckMutateValues();
    }

    /// <summary>Drops a row; the rows after it move up, and so do their check keys.</summary>
    private void RemoveMutateRow(int index)
    {
        if (Current.MutateRows.Count < 2 || index >= Current.MutateRows.Count)
        {
            return;
        }

        Current.MutateRows.RemoveAt(index);
        CheckMutateValues();
    }

    /// <summary>A new field takes a new value: what was typed for the old one is not its.</summary>
    private Task ChooseMutateFieldAsync(int index, string? field)
    {
        var row = Current.MutateRows[index];
        row.Field = field ?? string.Empty;
        row.Text = string.Empty;
        row.Empty = false;
        CheckMutateValues();
        return RefocusSelectAsync($"hook-mutate-field-{index}");
    }

    /// <summary>An enum row's value, chosen from its declared values.</summary>
    private Task ChooseMutateValueAsync(int index, string? value)
    {
        TypeMutateText(index, value);
        return RefocusSelectAsync(MutateValueId(index));
    }

    /// <summary>
    /// Gives focus back to a select once its choice is drawn: the library closes its list and swaps its box for the one
    /// that shows the value, and focus, left on the list's option, fell to <c>&lt;body&gt;</c> — outside the sheet, where
    /// Escape no longer reaches it (measured in a browser; a select inside a dialog is new here, spec §11).
    /// </summary>
    private Task RefocusSelectAsync(string testId) => Interop.FocusFirstOnceShownAsync([$"div[data-testid='{testId}']"]);

    private void ChooseMutateMode(int index, string word)
    {
        var row = Current.MutateRows[index];
        row.Mode = word == ExpressionWord ? MutateMode.Expression : MutateMode.Literal;
        row.Empty = false;
        CheckMutateValues();
    }

    private void TypeMutateText(int index, string? text)
    {
        Current.MutateRows[index].Text = text ?? string.Empty;
        _ = CheckMutateValueAsync(index);
    }

    private void SetMutateEmpty(int index, bool empty) => Current.MutateRows[index].Empty = empty;

    /// <summary>
    /// Asks about every row again — a row's index is part of its check key, and rows move — and clears each key no row
    /// holds any more, so a removed row's sentence, or one from a closed sheet, is never shown under another.
    /// </summary>
    private void CheckMutateValues()
    {
        for (var index = 0; index < Current.MutateRows.Count; index++)
        {
            _ = CheckMutateValueAsync(index);
        }

        for (var index = Current.MutateRows.Count; index < _mutateKeys; index++)
        {
            _ = CheckAsync(MutateValueId(index), string.Empty, (_, _) => null);
        }

        _mutateKeys = Current.MutateRows.Count;
    }

    /// <summary>One row's value, checked in a hook without the condition (spec D4); a literal is no CEL slot, so it clears.</summary>
    private Task CheckMutateValueAsync(int index)
    {
        var row = Current.MutateRows[index];
        var source = Current.Kind == HookBuilder.Mutate && row.Mode == MutateMode.Expression ? row.Text : string.Empty;
        var slot = HookBuilder.MutateSlot(row);
        return CheckAsync(MutateValueId(index), source, (copy, _) => slot[^1].Length == 0 ? null : ActionSlot(copy, slot));
    }

    /// <summary>A mutate always shows one row at least, so its first field is one choice away.</summary>
    private void EnsureMutateRow()
    {
        if (Current.Kind == HookBuilder.Mutate && Current.MutateRows.Count == 0)
        {
            Current.MutateRows.Add(new MutateRow());
        }
    }

    private IReadOnlyList<string> WritableFields() => Copy is { } copy ? HookFields.Writable(copy.Json, Entity) : [];

    private static string MutateValueId(int index) => $"hook-mutate-value-{index}";

    private static string ModeWord(MutateRow row) => row.Mode == MutateMode.Expression ? ExpressionWord : ValueWord;

    private FieldSchema? FieldOf(MutateRow row) => Current.Fields.GetValueOrDefault(HookBuilder.MutateKey(row));

    /// <summary>The writable fields, and — for a hook that names another — that one too, so it is shown rather than lost.</summary>
    private IReadOnlyList<string> MutateTargets(MutateRow row)
        => row.Field.Length == 0 || _writable.Contains(row.Field, StringComparer.Ordinal) ? _writable : [row.Field, .. _writable];

    private string TargetLabel(string target) => _writable.Contains(target, StringComparer.Ordinal) ? target : $"{target} (not offered)";

    private static IReadOnlyCollection<string> FlagSelected(MutateRow row) => row.Text.Length > 0 ? [row.Text] : [];

    /// <summary>The phone keyboard a literal box asks for: digits for a number, letters otherwise.</summary>
    private InputMode Keyboard(MutateRow row)
        => FieldOf(row)?.Type is FieldType.Integer or FieldType.Decimal ? InputMode.@decimal : InputMode.text;

    /// <summary>
    /// Why the row cannot be written — its field is not declared, or its literal does not fit it — computed on each render
    /// and focus-free (spec §4.3), so an opened hook says it before Save is pressed. A box nothing was typed into yet is
    /// not refused.
    /// </summary>
    private string? MutateFit(MutateRow row)
    {
        if (HookBuilder.MutateKey(row).Length == 0)
        {
            return null;
        }

        var untyped = row.Mode == MutateMode.Literal && row.Text.Length == 0 && !row.Empty && row.DeclaredKind is null;
        return FieldOf(row) is null || !untyped ? Current.RowRefusal(row) : null;
    }

    private string LiteralDescribedBy(MutateRow row, int index) => WithFit($"{MutateValueId(index)}-hint", row, index);

    /// <summary>The expression box's descriptions: its hint, the check's sentence while there is one, and the row's fit.</summary>
    private string ExpressionDescribedBy(MutateRow row, int index) => WithFit(Described(MutateValueId(index)), row, index);

    /// <summary>The fit sentence's id while there is one, for a box with no hint of its own to point at.</summary>
    private string? FitDescribedBy(MutateRow row, int index) => MutateFit(row) is null ? null : $"{MutateValueId(index)}-fit";

    private string WithFit(string described, MutateRow row, int index)
        => FitDescribedBy(row, index) is { } fit ? $"{described} {fit}" : described;
}
