using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules;

/// <summary>
/// One compiled lookup check: a forward relation over <paramref name="Lookup"/> whose body is the check's whole
/// condition, so the write passes only when the row the lookup names exists, the caller may list it, and the condition
/// holds for it.
/// </summary>
/// <param name="Lookup">The ref field the check follows; a null value skips the check.</param>
/// <param name="Relation">The forward relation over <paramref name="Lookup"/>; bound per caller by the policy engine.</param>
/// <param name="Message">The author's text, shown to the caller when the check fails.</param>
/// <param name="Reads">Every field of this entity the check reads, <paramref name="Lookup"/> included: an update re-runs the check only when one of them changed.</param>
internal sealed record DescriptorCheck(string Lookup, CelRelation Relation, string Message, IReadOnlySet<string> Reads);
