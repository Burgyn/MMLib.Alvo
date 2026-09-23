namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The fields one entity declares <c>readOnly</c>, split by whether the lock is certain.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dashboard cannot evaluate CEL, so an expression counts as a lock on an edit.</b> The form
/// would otherwise offer a control the engine refuses with <c>read-only-field</c> for exactly the callers
/// the expression exists for; showing the value read-only is wrong only for the callers it admits, and
/// they still have the API.
/// </para>
/// <para>
/// <b>Not on a create.</b> The engine refuses <c>required</c> with <c>readOnly: true</c> at apply, so a
/// conditional lock is the one way a required field can be locked at all — and locking it in the form
/// would make the create impossible for every caller, the admitted ones included. On a create the field
/// is offered, and the engine answers for the caller it is not for.
/// </para>
/// </remarks>
/// <param name="Always">Fields declared <c>"readOnly": true</c>.</param>
/// <param name="Conditional">Fields whose <c>readOnly</c> is a CEL expression.</param>
internal sealed record FieldLocks(IReadOnlySet<string> Always, IReadOnlySet<string> Conditional)
{
    /// <summary>An entity that locks nothing.</summary>
    public static FieldLocks None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Whether the form treats the field as read-only.</summary>
    /// <param name="field">The field.</param>
    /// <param name="creating">Whether the form creates a record rather than edits one.</param>
    public bool Locked(string field, bool creating)
        => Always.Contains(field) || (!creating && Conditional.Contains(field));
}
