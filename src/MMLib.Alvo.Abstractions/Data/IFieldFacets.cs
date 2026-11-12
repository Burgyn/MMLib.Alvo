using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data;

/// <summary>
/// Measures the values a data-port write supplies against their fields' declared facets — <c>maxLength</c>, enum
/// <c>values</c>, decimal precision and scale, <c>format</c> — once, in the core, before any driver sees them (issue 481).
/// </summary>
/// <remarks>
/// <para>
/// <b>A port rather than a driver check, as <see cref="Rules.IBeforeHookRunner"/> is,</b> because the measure lives in the
/// core — it is the payload validator's own (<c>RecordValidator.FacetViolation</c>), with the compiled formats a
/// <c>format</c> resolves to — while every caller is a driver, which depends on <c>MMLib.Alvo.Abstractions</c> alone. One
/// port makes every engine answer the same: before it, a host writing through <see cref="IAlvoData"/> stored an
/// out-of-enum value or an over-long string on SQLite, while PostgreSQL's <c>varchar(n)</c> refused the same write as an
/// unmapped <c>22001</c>.
/// </para>
/// <para>
/// <b>Internal, so a host cannot replace it</b> — a facet cannot be switched off by a registration (fail closed), the
/// <c>IEntityLifecycle</c> precedent.
/// </para>
/// <para>
/// <b>Only the values the write supplies, and only non-null ones.</b> A partial update is measured over the fields it
/// writes, like an HTTP PATCH; an omitted field keeps its stored value and a <see langword="null"/> is the
/// <c>required</c> rule's, which the engine's <c>NOT NULL</c> already answers the same everywhere. A key the write may not
/// name at all — a framework-managed, computed, virtual or undeclared field — is refused by the driver's payload guard
/// before this runs, and is skipped here.
/// </para>
/// </remarks>
internal interface IFieldFacets
{
    /// <summary>The refusal for the first value, in field declaration order, its field's facets do not admit.</summary>
    /// <param name="entity">The entity being written, as the applied schema declares it.</param>
    /// <param name="values">The values the write supplies.</param>
    /// <param name="unnameable">
    /// The fields the descriptor carries any <c>hidden</c> flag for (the write's <c>PolicyDecision.Unnameable</c>): a refusal
    /// for one names no field, facet or bound.
    /// </param>
    /// <returns>
    /// The <see cref="AlvoConstraintViolationException"/> of kind <see cref="AlvoConstraintKind.Check"/> to throw — or, in a
    /// batch, to report by the row's index — or <see langword="null"/> when every value fits.
    /// </returns>
    AlvoConstraintViolationException? Refusal(
        EntitySchema entity, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> unnameable);
}
