using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Components.Schema;

/// <summary>The entity's fields as the working copy declares them, for the hook editor's mutate rows (spec §4.3).</summary>
/// <remarks>
/// <para>
/// Read through <see cref="PendingSchema"/> — the reader the entity screen already draws a staged entity with — so a field
/// added a minute ago is offered and its type, values and <c>required</c> are the ones apply will see.
/// </para>
/// <para>
/// Every <see cref="FieldSchema.Type"/> it hands out is a named <see cref="FieldType"/> member: <see cref="PendingSchema"/>
/// reads a type by name only, so a consumer such as <c>ConditionTable.KindOf</c>, which throws on an undefined value,
/// is safe to call with it.
/// </para>
/// </remarks>
internal static class HookFields
{
    /// <summary>Every field the entity declares, by name.</summary>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="entity">The entity.</param>
    public static IReadOnlyDictionary<string, FieldSchema> Declared(string workingJson, string entity)
        => PendingSchema.Read(workingJson, entity)?.Fields
               .DistinctBy(field => field.Name, StringComparer.Ordinal)
               .ToDictionary(field => field.Name, StringComparer.Ordinal)
           ?? new Dictionary<string, FieldSchema>(StringComparer.Ordinal);

    /// <summary>The fields a mutate may patch, in declaration order.</summary>
    /// <remarks>
    /// Not a column the framework manages (apply refuses those, <c>BeforeHookCompiler.Target</c>), and not a computed or
    /// rollup field, which the database or the framework maintains — apply does not refuse a mutate of one (unverified what
    /// the write then does), so the form simply does not offer it.
    /// </remarks>
    /// <param name="workingJson">The working copy's text.</param>
    /// <param name="entity">The entity.</param>
    public static IReadOnlyList<string> Writable(string workingJson, string entity)
    {
        if (PendingSchema.Read(workingJson, entity) is not { } schema)
        {
            return [];
        }

        var managed = AlvoManagedColumns.For(schema);
        return [.. schema.Fields
            .Where(field => !managed.Contains(field.Name) && field.ComputedExpression is null && field.Rollup is null)
            .Select(field => field.Name)
            .Distinct(StringComparer.Ordinal)];
    }
}
