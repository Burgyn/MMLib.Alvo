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

    /// <summary>
    /// Rows go in through <see cref="WritePropertyBag"/>, exactly as the create path's do, so a seeded row and
    /// a created one are stored identically. A seam that prepared its values its own way would let a fixture
    /// reach a state production cannot — and, worse, miss one production can.
    /// </summary>
    private static void Add(AlvoDataContext context, string entity, IReadOnlyList<AlvoRecord> records)
    {
        var rows = context.Rows(entity);
        foreach (var record in records)
        {
            rows.Add(WritePropertyBag.For(rows.EntityType, record.Values));
        }
    }
}
