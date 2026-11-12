using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>The data port's facet measure (issue 481): every value a write supplies, against its field's declared facets.</summary>
internal sealed partial class EfAlvoData
{
    /// <summary>
    /// The core's facet measure, called on every write face after the caller's payload guard and before any candidate is
    /// built — so a host writing through this port meets the facets an HTTP body meets, and SQLite and PostgreSQL answer
    /// the same; see <see cref="IFieldFacets"/>. It measures a host before-write patch too, over the fields the patch sets:
    /// the core's runner already refuses a bad <c>Set</c> as <c>invalid-set</c>, but <see cref="IWriteExtensionRunner"/> is
    /// public and replaceable, and host code must not store through a runner what it cannot store through this port.
    /// </summary>
    private readonly IFieldFacets _facets;

    /// <summary>Refuses the write when a value it supplies breaks its field's declared facets.</summary>
    /// <param name="schema">The entity being written.</param>
    /// <param name="values">The caller's own payload.</param>
    /// <param name="unnameable">The fields a refusal never names (the write's decision).</param>
    /// <exception cref="AlvoConstraintViolationException">A value breaks a facet (<see cref="AlvoConstraintKind.Check"/>).</exception>
    private void EnsureFacetsFit(EntitySchema schema, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> unnameable)
    {
        if (_facets.Refusal(schema, values, unnameable) is { } refused)
        {
            throw refused;
        }
    }

    /// <summary>
    /// One batch row's facet refusal, by its index: the facet's own code and wording, or the fixed validation wording and
    /// <see cref="CheckCode"/> for a field the descriptor hides.
    /// </summary>
    private AlvoRowRefusal? FacetRefusal(int index, EntitySchema schema, IReadOnlyDictionary<string, object?> values, PolicyDecision decision) =>
        _facets.Refusal(schema, values, decision.Unnameable) is { } refused ? FacetRowRefusal(index, refused) : null;

    private static AlvoRowRefusal FacetRowRefusal(int index, AlvoConstraintViolationException refused) =>
        new(index, refused.FacetCode ?? CheckCode, refused.Message, refused.FacetFix);
}
