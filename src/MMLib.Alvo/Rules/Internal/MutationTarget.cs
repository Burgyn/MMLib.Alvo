using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The field a <c>mutate</c> writes, with what its value is measured against: the field's declared facets and
/// the compiled formats a <c>format</c> facet resolves to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ruling V (#308): a hook's value honours the same facets a caller's does.</b> Before it, the compiler checked
/// only the CEL <em>type</em>, so a <c>replace</c> grown past <c>maxLength</c>, an enum field set to a value the
/// enum does not declare, or a host function's 5,000-character answer were stored on SQLite (which enforces no
/// length) and refused by PostgreSQL's <c>varchar(n)</c> as an anonymous 500 — one descriptor, two answers.
/// Measuring the value in the core, before any driver sees the patch, gives every engine the same answer.
/// </para>
/// <para>
/// <b>The checks are <see cref="RecordValidator.FacetViolation"/>, not a copy of them</b>, plus the one the
/// payload validator spells separately: a <see langword="null"/> into a <c>required</c> field. An integer into a
/// <c>decimal</c> field — the one widening <c>BeforeHookCompiler</c> admits — is measured as the decimal it is
/// stored as, or its precision would go unchecked.
/// </para>
/// </remarks>
/// <param name="Field">The declared field the mutation writes.</param>
/// <param name="Formats">The compiled formats of the entity the field belongs to.</param>
internal sealed record MutationTarget(FieldSchema Field, FormatCatalog Formats)
{
    /// <summary>The first facet <paramref name="value"/> violates, or <see langword="null"/> when it fits.</summary>
    /// <param name="value">The value the mutation would store, in the field's own CLR representation.</param>
    internal AlvoViolation? Violation(object? value) => value is null
        ? Field.Required ? PayloadViolations.Required(Field) : null
        : RecordValidator.FacetViolation(Field, Stored(value), Formats);

    private object Stored(object value) =>
        Field.Type == FieldType.Decimal && value is long whole ? (decimal)whole : value;
}
