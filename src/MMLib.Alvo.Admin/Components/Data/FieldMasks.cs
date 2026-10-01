namespace MMLib.Alvo.Admin.Components.Data;

/// <summary>
/// The fields one entity declares <c>hidden</c>, split by whether the mask is certain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the descriptor, because the resolved schema does not carry it.</b> A mask is policy,
/// not shape: <c>FieldSchema</c> has no <c>hidden</c>, and the one place that knows is the
/// descriptor the apply accepted.
/// </para>
/// <para>
/// <b>Two sets, because the grid asks two different questions.</b> A field hidden with
/// <c>true</c> never comes back, so it is never a column or a label. A field hidden by a CEL
/// expression comes back for some callers and not for others, and the dashboard cannot evaluate
/// CEL — so it may still be a label (a caller it hides from gets the short id), but it is never put
/// into a search or a sort, because the data port refuses the <em>whole query</em> when a term
/// names a field the caller cannot read.
/// </para>
/// </remarks>
/// <param name="Always">Fields declared <c>"hidden": true</c>.</param>
/// <param name="Conditional">Fields whose <c>hidden</c> is a CEL expression.</param>
internal sealed record FieldMasks(IReadOnlySet<string> Always, IReadOnlySet<string> Conditional)
{
    /// <summary>An entity that masks nothing.</summary>
    public static FieldMasks None { get; } = new(
        new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Whether the field is withheld from every caller.</summary>
    public bool NeverReturned(string field) => Always.Contains(field);

    /// <summary>Whether the field is withheld from at least some callers.</summary>
    public bool MayBeHidden(string field) => Always.Contains(field) || Conditional.Contains(field);
}
