using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What a hook's <c>Set</c> may write (Ruling 16): a declared field — or <c>tenant_id</c>, judged by the rules afterwards — that
/// Alvo does not maintain, a value of the field's type, inside its facets (Ruling V, through <see cref="MutationTarget"/>).
/// </summary>
internal static class HookSets
{
    /// <summary>The problem of a <c>Set</c> of a field its entity does not declare — or of any <c>Set</c> on an entity with no schema.</summary>
    internal const string Undeclared = "undeclared";

    /// <summary>
    /// <paramref name="sets"/> with every lossless widening applied (Ruling 47): an integral CLR value is already a <c>long</c>
    /// (<c>RecordWriter.Field</c>), a <c>float</c> becomes a <c>double</c>, and a whole number into a <c>decimal</c> field
    /// becomes a <c>decimal</c>. Nothing lossy is converted; it stays for <see cref="FirstProblem"/> to refuse.
    /// </summary>
    internal static Dictionary<string, object?> Coerced(IReadOnlyDictionary<string, object?> sets, EntitySchema entity) =>
        sets.ToDictionary(entry => entry.Key, entry => Coerce(entity.Fields.FirstOrDefault(field => field.Name == entry.Key), entry.Value), StringComparer.Ordinal);

    internal static (string Field, string Problem)? FirstProblem(IReadOnlyDictionary<string, object?> sets, EntitySchema entity)
    {
        FormatCatalog? formats = null;
        foreach (var (name, value) in Coerced(sets, entity))
        {
            if (Problem(name, value, entity, () => formats ??= FormatCatalog.Build([entity])) is { } problem)
            {
                return (name, problem);
            }
        }

        return null;
    }

    private static object? Coerce(FieldSchema? field, object? value) => (field?.Type, value) switch
    {
        (_, float single) => (double)single,
        (FieldType.Decimal, long whole) => (decimal)whole,
        _ => value,
    };

    private static string? Problem(string name, object? value, EntitySchema entity, Func<FormatCatalog> formats)
    {
        if (name == AlvoManagedColumns.TenantId)
        {
            return entity.Tenancy != TenancyMode.Scoped ? Undeclared : value is null or Guid ? null : "type";
        }

        if (entity.Fields.FirstOrDefault(field => field.Name == name) is not { } field)
        {
            return Undeclared;
        }

        return Maintained(field, entity) ? "maintained"
            : !Holds(field, value) ? "type"
            : new MutationTarget(field, formats(), Disclosable: true).Violation(value)?.Code;
    }

    private static bool Maintained(FieldSchema field, EntitySchema entity) =>
        field.Name == AlvoManagedColumns.Id
        || field.Name == AlvoManagedColumns.DeletedAt
        || AlvoManagedColumns.Audit.Contains(field.Name)
        || field.Name == AlvoManagedColumns.VersionColumn(entity)
        || field.ComputedExpression is not null
        || field.Rollup is not null;

    private static bool Holds(FieldSchema field, object? value) => value is null || FieldClrType.Of(field).IsInstanceOfType(value);
}
