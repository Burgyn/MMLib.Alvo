namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Inserts rows <b>bypassing policy entirely</b>, through the property-bag change tracker so every value
/// is stored in exactly the representation EF's own type mapping produces. Exists for the inherited
/// adversarial suite, whose fixtures deliberately seed rows a policy-respecting write could never
/// produce — two owners in one call, entities that declare no <c>create</c> rule at all.
/// </summary>
/// <remarks>
/// <para>
/// <see langword="internal"/>, and visible only to this package's own tests: it is the one code path here
/// that writes without consulting <c>IPolicyEngine</c>, so it must not be reachable from a host. Seeding
/// through hand-rolled ADO.NET instead is what produced the de-risking spike's first false negative —
/// a hand-formatted <see cref="Guid"/> that no query could then match.
/// </para>
/// <para>
/// <b>That guard is a convention, not a boundary, and this is the note that says so.</b> The only thing
/// between a host and this type is <c>InternalsVisibleTo</c> on three test-project names, and no assembly in
/// this repository is signed — there is no <c>SignAssembly</c> or <c>PublicSign</c> anywhere in the build
/// props. An unsigned <c>InternalsVisibleTo</c> grants access by <em>name</em> alone, so an assembly calling
/// itself <c>MMLib.Alvo.Data.EntityFrameworkCore.Tests</c>, <c>MMLib.Alvo.Data.Sqlite.Tests</c> or
/// <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c> reaches a policy-free writer, and reflection reaches
/// it without even that. What would make it a boundary is a <b>strong name</b>: signing the shipped assemblies
/// turns <c>InternalsVisibleTo</c> into a public-key match rather than a string match. That is deliberately
/// not done here — signing is a decision about the whole package family's identity and binary-compatibility
/// story, not a detail of the data path — and it is filed as a follow-up. Until then the honest description
/// of this seam is "test-only by agreement", which is why it stays <see langword="internal"/>, appears in no
/// registration, and is excluded from the mutation configs (mutating it measures the harness, not the
/// product). The same caveat applies to every other <c>InternalsVisibleTo</c> in the family, and is already
/// recorded on <c>CompiledExpression</c> and <c>PolicyDecision</c>; it matters most here, because this is the
/// only one of them that <em>writes</em>.
/// </para>
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
