using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Collections.Frozen;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Resolves a caller-supplied field name against the entity the request is addressing <em>and</em> against
/// the field mask their policy resolved — returning the <b>declared</b> <see cref="FieldSchema"/>, so every
/// name that travels onwards is one the schema owns rather than the caller's own bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Undeclared and hidden are one answer, by construction.</b> A single <see langword="null"/> return is
/// what makes the two indistinguishable: a caller must not be able to tell "there is no such field" from
/// "there is, and you may not read it", and a design where the parser learns which of the two it was is one
/// refactor away from saying so. §2.1's warning is that a filter over a hidden field leaks that field's
/// value one comparison at a time (<c>salary.gt.&lt;x&gt;</c>, repeated, is a binary search), so the mask
/// has to be consulted <em>here</em> — refusing later, in the port, produces a 403 where an unknown field
/// produces a 422, and that difference is the oracle itself.
/// </para>
/// <para>
/// <b>Why the API layer resolves the policy at all.</b> The mask is <see cref="PolicyDecision.HiddenFields"/>,
/// which only <see cref="IPolicyEngine"/> can answer, so the list endpoint resolves the decision before
/// parsing. It uses <em>nothing else</em> from that decision: whether the caller may list at all stays the
/// port's answer, resolved again there, so this layer still neither re-checks nor bypasses an authorization
/// decision. A denied decision carries an empty mask, which is the fail-open-looking but correct direction —
/// the port refuses the whole read before a field name matters.
/// </para>
/// <para>
/// Ordinal, like every other field lookup in Alvo: the schema, the CEL type checker and the rendered SQL all
/// use the exact declared name, and a case-insensitive match here would admit a name none of them agreed to.
/// </para>
/// </remarks>
/// <param name="entity">The entity being queried, as the applied schema declares it.</param>
/// <param name="hiddenFields">The field mask the caller's policy resolved.</param>
internal sealed class QueryFieldResolver(EntitySchema entity, IReadOnlySet<string> hiddenFields)
{
    private readonly FrozenDictionary<string, FieldSchema> _available = entity.Fields
        .Where(field => !hiddenFields.Contains(field.Name))
        .ToFrozenDictionary(field => field.Name, StringComparer.Ordinal);

    /// <summary>
    /// The declared field <paramref name="name"/> refers to, or <see langword="null"/> when this caller has
    /// no such field — whether because the entity declares none, or because their mask hides it.
    /// </summary>
    /// <param name="name">The caller-supplied field name.</param>
    internal FieldSchema? Resolve(string name) =>
        _available.TryGetValue(name, out var field) ? field : null;
}
