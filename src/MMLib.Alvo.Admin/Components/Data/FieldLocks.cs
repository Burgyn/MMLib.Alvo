namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// The fields one entity declares <c>readOnly</c>, split by whether the lock is certain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only <c>true</c> locks the form.</b> A field declared <c>"readOnly": true</c> is frozen for every
/// caller, so it is shown under "Calculated" and never offered as a control.
/// </para>
/// <para>
/// <b>A CEL lock stays a control, on an edit as on a create.</b> The dashboard cannot evaluate CEL, so it
/// cannot know whether this caller is one the expression admits, and locking the field would take the
/// edit away from exactly those callers (a manager's <c>purchase_price</c>). A save sends only what
/// changed (<see cref="RecordDraft"/>), so leaving the field alone never writes it. A caller the
/// expression freezes who does change it gets the engine's <c>read-only-field</c> refusal in the sheet.
/// On a create the choice is forced anyway: the engine refuses <c>required</c> with <c>readOnly: true</c>
/// at apply, so a CEL lock is the only way a required field can be locked, and locking it in the form
/// would make the create impossible for every caller.
/// </para>
/// </remarks>
/// <param name="Always">Fields declared <c>"readOnly": true</c>.</param>
/// <param name="Conditional">Fields whose <c>readOnly</c> is a CEL expression.</param>
internal sealed record FieldLocks(IReadOnlySet<string> Always, IReadOnlySet<string> Conditional)
{
    /// <summary>An entity that locks nothing.</summary>
    public static FieldLocks None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Whether the form shows the field read-only rather than as a control.</summary>
    public bool Locked(string field) => Always.Contains(field);

    /// <summary>Whether the engine may refuse a write of the field for some callers and not others.</summary>
    public bool LockedForSome(string field) => Conditional.Contains(field);
}
