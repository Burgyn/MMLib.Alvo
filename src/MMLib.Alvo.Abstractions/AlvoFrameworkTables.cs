namespace MMLib.Alvo;

/// <summary>
/// <b>The one authority on what Alvo's own bookkeeping tables are called</b>, for a given
/// <see cref="AlvoOptions.SchemaPrefix"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In Abstractions because two layers that cannot see each other need the same answer.</b> The tables
/// are created and read by the Entity Framework Core provider adapter; the descriptor is validated in the
/// core, which must not reference a provider (§0 principle 2). Before this type the names lived only in the
/// adapter, so the core had nothing to reserve them against and an entity called <c>alvo_outbox</c> mapped
/// straight onto the outbox — the framework and a user entity believing they owned one table (#156).
/// </para>
/// <para>
/// <b>A suffix table rather than a name per caller.</b> Every place that names one of these tables spells
/// it from here, so a fourth framework table reserves itself, is excluded from introspection, and is
/// refused as an entity name by being added once. The failure the shape prevents is silent in both
/// directions: a name the introspector does not know about is planned for <c>DROP</c> on the next
/// re-apply, and a name the validator does not know about is quietly co-owned.
/// </para>
/// <para>
/// <b>Internal, not public, and that is the decision rather than an oversight.</b> The information was
/// never public before this type existed either — it lived on <c>SystemSchemaInitializer</c>, an
/// <c>internal</c> class — so publishing it here would have been a new commitment, not a move. The shipped
/// seam for a new engine is <c>IAlvoSqlDialect</c>, which plugs in <em>under</em> the Entity Framework Core
/// adapter and never sees a table name: the adapter does the creating and the introspection-excluding for
/// every dialect. The one consumer who would need this is someone writing an
/// <see cref="Schema.ISchemaIntrospector"/> from scratch, and nothing in this repository is evidence that
/// anyone does. Making it public later is additive; making a <c>const</c> internal again is breaking, so
/// the asymmetry settles it — and a public <c>const</c> would be inlined into a consumer at compile time,
/// which is the sharper half: a renamed suffix would break them silently, on upgrade, until they rebuilt.
/// </para>
/// <para>
/// The names are lower snake_case by construction — <see cref="AlvoOptions.SchemaPrefix"/> is validated
/// against <c>^[a-z][a-z0-9_]{0,15}$</c> and the suffixes are literals — so a caller interpolating one
/// into DDL is placing a validated identifier, never caller-supplied data.
/// </para>
/// </remarks>
internal static class AlvoFrameworkTables
{
    /// <summary>The suffix of the table holding the append-only descriptor-version history.</summary>
    internal const string DescriptorVersionsSuffix = "_descriptor_versions";

    /// <summary>The suffix of the table holding idempotency records.</summary>
    internal const string IdempotencySuffix = "_idempotency";

    /// <summary>The suffix of the table holding the transactional outbox.</summary>
    internal const string OutboxSuffix = "_outbox";

    /// <summary>
    /// Every table the framework owns under <paramref name="schemaPrefix"/> — the set an introspector
    /// excludes from the user's schema and the set an entity name may not collide with.
    /// </summary>
    /// <param name="schemaPrefix">The validated <see cref="AlvoOptions.SchemaPrefix"/>.</param>
    /// <returns>The fully-prefixed table names, in no significant order.</returns>
    internal static IReadOnlyList<string> NamesFor(string schemaPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaPrefix);

        return
        [
            schemaPrefix + DescriptorVersionsSuffix,
            schemaPrefix + IdempotencySuffix,
            schemaPrefix + OutboxSuffix,
        ];
    }
}
