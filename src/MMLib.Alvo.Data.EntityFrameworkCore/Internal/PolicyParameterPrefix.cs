namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Every bind-parameter name family the data path generates, kept disjoint from each other and from the
/// <c>pN</c> names EF Core mints for its own positional <c>FromSql</c> arguments and
/// <c>ExecuteUpdate</c> setters.
/// </summary>
/// <remarks>
/// The collision this exists to prevent does not raise an error: given a name EF also wants, EF renames
/// the caller's parameter while the SQL text still reads the original name, so the other value is
/// substituted into the security predicate — on PostgreSQL that usually surfaces as a type error, on
/// SQLite it returns the wrong rows silently. A <c>PolicyDecision</c> carries three predicates, so it
/// needs three prefixes, and the two statement-level families and the row id need names of their own.
/// </remarks>
internal static class PolicyParameterPrefix
{
    /// <summary>The prefix for the <c>USING</c> predicate's parameters.</summary>
    internal const string Using = "alvo_u";

    /// <summary>
    /// The prefix reserved for the <c>WITH CHECK</c> predicate's parameters. Unused today — the check is
    /// evaluated in memory over the merged post-image, which SQL cannot see before the write — and
    /// reserved so a future SQL-side check (a <c>RETURNING</c>-based write, say) inherits a name that
    /// already cannot collide with the other two.
    /// </summary>
    internal const string WithCheck = "alvo_c";

    /// <summary>The prefix for the synthesized tenant scope's parameters.</summary>
    internal const string TenantScope = "alvo_t";

    /// <summary>The prefix for the caller filter's bound values.</summary>
    internal const string Filter = "alvo_f";

    /// <summary>The prefix for the keyset cursor predicate's bound values.</summary>
    internal const string Keyset = "alvo_k";

    /// <summary>The single name a row id binds to.</summary>
    internal const string RowId = "alvo_id";

    /// <summary>
    /// The single name a page's row limit binds to. Bound rather than formatted into the text, like every
    /// other value this data path puts in a statement.
    /// </summary>
    internal const string RowLimit = "alvo_limit";

    /// <summary>
    /// Every reserved name, for the disjointness invariant. Every <see langword="const"/> <see cref="string"/>
    /// this type declares is a reserved name and belongs here — the invariant test reflects over all of them,
    /// so a message or format constant added to this type would fail it rather than escape it.
    /// </summary>
    internal static IReadOnlyList<string> All { get; } =
        [Using, WithCheck, TenantScope, Filter, Keyset, RowId, RowLimit];
}
