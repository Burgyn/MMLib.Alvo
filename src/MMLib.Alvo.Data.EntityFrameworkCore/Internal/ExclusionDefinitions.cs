using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>Resolves an entity's exclusion index into the <see cref="ExclusionConstraintDefinition"/> a dialect is asked for.</summary>
internal static class ExclusionDefinitions
{
    /// <summary>The definition of <paramref name="entity"/>'s exclusion <paramref name="index"/>.</summary>
    /// <param name="entity">The declaring entity.</param>
    /// <param name="index">An index declaring <see cref="IndexSchema.Exclude"/>.</param>
    /// <param name="filters">Renders the index's <c>where</c>, required when it declares one.</param>
    /// <exception cref="InvalidOperationException">The index declares a <c>where</c> and no renderer was supplied.</exception>
    internal static ExclusionConstraintDefinition For(EntitySchema entity, IndexSchema index, IndexFilterSql? filters)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(index);
        var range = index.Exclude ?? throw new ArgumentException("The index declares no exclusion.", nameof(index));

        return new ExclusionConstraintDefinition(
            entity.Name,
            IndexNames.Exclusion(entity, index),
            DescriptorModelBuilder.UniqueColumns(entity, index.Fields),
            range.From,
            range.To,
            RangeType(entity, range),
            Filter(entity, index, filters));
    }

    private static FieldType RangeType(EntitySchema entity, ExclusionSchema range) =>
        entity.Fields.First(field => string.Equals(field.Name, range.From, StringComparison.Ordinal)).Type;

    private static string? Filter(EntitySchema entity, IndexSchema index, IndexFilterSql? filters) => index.Where is null
        ? null
        : (filters ?? throw new InvalidOperationException(
            $"An index of '{entity.Name}' declares 'where', which is rendered from CEL — but this container registered no "
            + "expression services. Call AddAlvo() on the same service collection as UseSqlite()/UsePostgreSql().")).For(entity, index);
}
