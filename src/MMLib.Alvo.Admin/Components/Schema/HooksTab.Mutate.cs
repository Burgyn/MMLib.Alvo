using MMLib.Alvo.Schema;

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
        return RefocusSelectAsync(FieldSelectId(index));
    }

    /// <summary>An enum row's value, chosen from its declared values.</summary>
    private Task ChooseMutateValueAsync(int index, string? value)
    {
        TypeMutateText(index, value);
        return RefocusSelectAsync(MutateValueId(index));
    }

    private void ChooseMutateMode(int index, string word)
    {
        var row = Current.MutateRows[index];
        row.Mode = word == ExpressionWord ? MutateMode.Expression : MutateMode.Literal;
        row.Empty = false;
        CheckMutateValues();
    }

    /// <summary>A value typed or chosen: the row writes it, so it no longer sets the field to empty.</summary>
    private void TypeMutateText(int index, string? text)
    {
        var row = Current.MutateRows[index];
        row.Text = text ?? string.Empty;
        row.Empty = false;
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

    private static string FieldSelectId(int index) => $"hook-mutate-field-{index}";

    private static string ModeWord(MutateRow row) => row.Mode == MutateMode.Expression ? ExpressionWord : ValueWord;

    private FieldSchema? FieldOf(MutateRow row) => Current.Fields.GetValueOrDefault(HookBuilder.MutateKey(row));

    /// <summary>The writable fields, and — for a hook that names another — that one too, so it is shown rather than lost.</summary>
    private IReadOnlyList<string> MutateTargets(MutateRow row)
        => row.Field.Length == 0 || _writable.Contains(row.Field, StringComparer.Ordinal) ? _writable : [row.Field, .. _writable];

    private string TargetLabel(string target) => _writable.Contains(target, StringComparer.Ordinal) ? target : $"{target} (not offered)";

    /// <summary>The chip that reads as chosen: none while Set to empty is ticked, because the row then writes no flag.</summary>
    private static IReadOnlyCollection<string> FlagSelected(MutateRow row) => row.Empty || row.Text.Length == 0 ? [] : [row.Text];

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

    /// <summary>A literal box's descriptions: its hint, and the row's fit sentence while there is one.</summary>
    /// <param name="index">The row's index.</param>
    /// <param name="fit">The row's <see cref="MutateFit"/>, computed once per row and render.</param>
    private static string LiteralDescribedBy(int index, string? fit) => WithFit($"{MutateValueId(index)}-hint", index, fit);

    /// <summary>The expression box's descriptions: its hint, the check's sentence while there is one, and the row's fit.</summary>
    private string ExpressionDescribedBy(int index, string? fit) => WithFit(Described(MutateValueId(index)), index, fit);

    /// <summary>The fit sentence's id while there is one, for a group with no hint of its own to point at.</summary>
    private static string? FitDescribedBy(int index, string? fit) => fit is null ? null : $"{MutateValueId(index)}-fit";

    private static string WithFit(string described, int index, string? fit)
        => FitDescribedBy(index, fit) is { } id ? $"{described} {id}" : described;
}
