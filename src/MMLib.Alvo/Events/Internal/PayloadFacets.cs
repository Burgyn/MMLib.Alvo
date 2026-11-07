using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// What an <c>entity.update</c> payload value is measured against: its target field's declared facets, the same
/// <see cref="MutationTarget"/> a before-hook <c>mutate</c> uses (Ruling V) — at apply for a literal, at run time for an
/// expression's result (Ruling Q22).
/// </summary>
/// <remarks>
/// The data port measures no facet (its contract leaves that to the HTTP layer's <c>RecordValidator</c>), so without this a
/// value past <c>maxLength</c> would be stored by SQLite and refused by PostgreSQL's <c>varchar(n)</c> — one descriptor,
/// two answers. Each entity's formats are compiled once per schema instance.
/// </remarks>
internal static class PayloadFacets
{
    private static readonly ConditionalWeakTable<EntitySchema, FormatCatalog> _formats = [];

    /// <summary>The measure for <paramref name="field"/> of <paramref name="entity"/>.</summary>
    /// <param name="field">The declared target field.</param>
    /// <param name="entity">The entity the field belongs to.</param>
    /// <param name="descriptor">
    /// The descriptor, which decides whether a refusal may name the field (Ruling X); <see langword="null"/> discloses
    /// nothing.
    /// </param>
    internal static MutationTarget TargetOf(FieldSchema field, EntitySchema entity, AlvoDescriptor? descriptor) =>
        new(field, _formats.GetValue(entity, static schema => FormatCatalog.Build([schema])), Disclosable(descriptor, entity.Name, field.Name));

    private static bool Disclosable(AlvoDescriptor? descriptor, string entity, string field) =>
        descriptor?.Entities.GetValueOrDefault(entity)?.Fields?.GetValueOrDefault(field) is { } declared
        && (declared.Hidden is null || declared.Hidden.Boolean == false);
}
