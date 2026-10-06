using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The guided condition and the switch to text (spec §4.6, §7; ruling B7). */
public partial class HooksTab
{
    private const string GuidedWord = "Guided";
    private const string TextWord = "Text";
    private static readonly string[] _conditionModes = [GuidedWord, TextWord];
    private bool _guided = true;
    private string? _conditionNote;

    /// <summary>Text always; rows only when the condition is exactly what the rows write — otherwise it says so and stays text.</summary>
    private void ChooseConditionMode(string mode)
    {
        _conditionNote = null;
        if (mode == TextWord)
        {
            _guided = false;
        }
        else if (Recognizes(Current.Condition))
        {
            _guided = true;
        }
        else
        {
            _conditionNote = "This condition cannot be shown as rows; it stays as text.";
        }
    }

    /// <summary>On open: rows for a condition the rows write (or none), text for anything else (spec §4.2).</summary>
    private void SettleConditionMode()
    {
        _conditionNote = null;
        _guided = Recognizes(Current.Condition);
    }

    /// <summary>After a point change: rows that no longer fit the point fall back to text, and say so.</summary>
    private void KeepConditionModeFitting()
    {
        if (_guided && !Recognizes(Current.Condition))
        {
            _guided = false;
            _conditionNote = $"The rows do not fit {Current.Point}; the condition is kept as text.";
        }
    }

    private bool Recognizes(string condition)
        => Copy is { } copy
           && ConditionText.Recognize(
               condition,
               Current.Point,
               ConditionScope.From(PendingSchema.Read(copy.Json, Entity), DescriptorLens.DeclaredRoles(copy.Json))) is not null;
}
