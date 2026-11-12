using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Schema;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Data.Internal;

/// <summary>
/// The core's <see cref="IFieldFacets"/>: the payload validator's facet checks (<see cref="RecordValidator.FacetViolation"/>)
/// over the values a data-port write supplies, so a host write meets the facets an HTTP body meets (issue 481).
/// </summary>
/// <remarks>
/// <para>
/// <b>The third consumer of the one definition of "a value fits its field"</b>, beside a caller's HTTP payload and a
/// before-hook's <c>mutate</c> (Ruling V). It reports the first violation only — the port answers one refusal per write,
/// as every other port refusal does — and HTTP keeps its own pass, which reports every violation with body pointers, so
/// an HTTP write that reaches the port has already passed this measure and its response is unchanged.
/// </para>
/// <para>
/// <b>Ruling X holds here too:</b> a field the descriptor carries any <c>hidden</c> flag for is refused with the fixed
/// validation wording and no field, so the refusal publishes neither its name, its facet nor its bound.
/// </para>
/// </remarks>
internal sealed class PortFieldFacets : IFieldFacets
{
    /// <inheritdoc/>
    public AlvoConstraintViolationException? Refusal(
        EntitySchema entity, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> unnameable)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(unnameable);

        foreach (var field in Measured(entity, values))
        {
            if (RecordValidator.FacetViolation(field, values[field.Name]!, PayloadFacets.FormatsOf(entity)) is { } violation)
            {
                return Refused(field, violation, unnameable);
            }
        }

        return null;
    }

    /// <summary>The declared fields the write supplies a non-null value for, in declaration order; managed and virtual ones excluded.</summary>
    private static IEnumerable<FieldSchema> Measured(EntitySchema entity, IReadOnlyDictionary<string, object?> values) =>
        _measurable.GetValue(entity, Measurable).Where(field => values.TryGetValue(field.Name, out var value) && value is not null);

    /// <summary>The fields a write may carry a caller's value for, computed once per schema instance — the hot path runs per write.</summary>
    private static FieldSchema[] Measurable(EntitySchema entity)
    {
        var managed = AlvoManagedColumns.For(entity);
        return [.. entity.Fields.Where(field => !managed.Contains(field.Name) && !field.IsVirtual)];
    }

    private static readonly ConditionalWeakTable<EntitySchema, FieldSchema[]> _measurable = [];

    private static AlvoConstraintViolationException Refused(
        FieldSchema field, AlvoViolation violation, IReadOnlySet<string> unnameable) =>
        unnameable.Contains(field.Name)
            ? new AlvoConstraintViolationException(AlvoConstraintKind.Check, [])
            : new AlvoConstraintViolationException(field.Name, violation.Code, violation.Message, violation.FixSuggestion);
}
