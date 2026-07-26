namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Inserts rows <b>bypassing policy entirely</b>, through the property-bag change tracker so every value
/// is stored in exactly the representation EF's own type mapping produces. Exists for the inherited
/// adversarial suite, whose fixtures deliberately seed rows a policy-respecting write could never
/// produce — two owners in one call, entities that declare no <c>create</c> rule at all.
/// </summary>
/// <remarks>
/// <see langword="internal"/>, and visible only to this package's own tests: it is the one code path here
/// that writes without consulting <c>IPolicyEngine</c>, so it must not be reachable from a host. Seeding
/// through hand-rolled ADO.NET instead is what produced the de-risking spike's first false negative —
/// a hand-formatted <see cref="Guid"/> that no query could then match.
/// </remarks>
internal static class AlvoDataSeed
{
    internal static async Task SeedAsync(
        AlvoDataContextFactory contexts,
        IReadOnlyDictionary<string, IReadOnlyList<AlvoRecord>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(rows);

        using var context = contexts.Create();
        foreach (var (entity, records) in rows)
        {
            Add(context, entity, records);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static void Add(AlvoDataContext context, string entity, IReadOnlyList<AlvoRecord> records)
    {
        foreach (var record in records)
        {
            context.Rows(entity).Add(SetValues(record));
        }
    }

    /// <summary>
    /// A property bag carries only the fields it sets, and a <see langword="null"/> entry is dropped
    /// rather than written: the bag's value type is non-nullable, and an absent key already means "leave
    /// the column at its default", which for a nullable column is <c>NULL</c>.
    /// </summary>
    private static Dictionary<string, object> SetValues(AlvoRecord record) =>
        new(
            record.Values.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!),
            StringComparer.Ordinal);
}
