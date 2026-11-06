using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The apply-time recompute of every rollup a plan adds to an existing parent or changes (HX-11 Ruling 32): one correlated
/// <c>UPDATE parent SET f = (&lt;sub&gt;) WHERE f IS DISTINCT FROM (&lt;sub&gt;)</c> per field, run after EF's DDL in the
/// apply's own transaction, so a parent never reads the old definition's aggregate — or, for a rollup added to a populated
/// table, nothing — until a child write happens to recompute it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same aggregate as the write path.</b> The subquery is <see cref="RollupRecompute.Aggregate"/> over the child,
/// narrowed by the foreign key, by <c>tenant_id</c> when the pair is scoped, and by the filter
/// <see cref="RollupRecompute.Filter"/> renders — so a value the backfill writes is the value the next child write would
/// recompute. The tenant predicate is what keeps one tenant's parent from aggregating another tenant's children that name
/// it, as the write path's does.
/// </para>
/// <para>
/// <b>The child is aliased, the parent is named.</b> The filter's columns are unqualified (Ruling 31) and resolve to the
/// innermost scope, the aliased child; the parent's key is qualified with its table name, which the alias leaves to the
/// outer <c>UPDATE</c> — also when a rollup aggregates its own entity.
/// </para>
/// <para>
/// <b>Only parents that change are written</b>, through the dialect's NULL-safe "differs"
/// (<see cref="IAlvoSqlDialect.RenderDistinct"/>): an unchanged parent costs neither a row version nor a row lock.
/// </para>
/// <para>
/// <b>The cost of that, recorded rather than optimised (Task 11 review m3).</b> The correlated subquery is rendered twice —
/// in <c>SET</c> and in the "differs" test — so it runs twice per parent row, and on PostgreSQL the child's <c>SHARE</c> lock
/// (Ruling 74) is held for roughly twice the aggregate time over a large parent. The rewrite that runs it once is
/// <c>UPDATE p SET f = s.v FROM (SELECT id, (&lt;sub&gt;) AS v FROM p) s WHERE p.id = s.id AND &lt;differs(p.f, s.v)&gt;</c>
/// (PostgreSQL, and SQLite from 3.33). It is declined for now: the backfill runs once per rollup-definition change, not per
/// write, and one statement shape is what both engines share; take it when an apply over a large parent shows the lock in a
/// measurement.
/// </para>
/// <para>
/// <b>No argument, no placeholder.</b> Every constant is the descriptor author's, rendered inline; the statements go into
/// <c>MigrationPlan.Sql</c> verbatim and are executed as plain command text, so the filter needs no brace escaping here.
/// </para>
/// </remarks>
internal static class RollupBackfill
{
    private const string ChildAlias = "alvo_backfill_child";

    /// <summary>One backfill statement.</summary>
    /// <param name="Entity">The parent entity whose field is recomputed.</param>
    /// <param name="Field">The rollup field.</param>
    /// <param name="Child">The aggregated entity, whose writes the apply must hold off on an engine that queues them.</param>
    /// <param name="Sql">The statement.</param>
    internal readonly record struct Statement(string Entity, string Field, EntitySchema Child, string Sql);

    /// <summary>One rollup the apply has to recompute.</summary>
    /// <param name="Parent">The desired parent entity.</param>
    /// <param name="Field">The desired rollup field.</param>
    /// <param name="Child">The desired aggregated entity.</param>
    internal readonly record struct Target(EntitySchema Parent, FieldSchema Field, EntitySchema Child);

    /// <summary>
    /// Every rollup of <paramref name="desired"/> whose parent table exists in <paramref name="current"/> and whose
    /// definition — <c>from</c>, <c>op</c>, <c>field</c>, <c>via</c>, <c>where</c> — differs from, or is absent in, the current
    /// field of that name: the values the table holds for it are not this definition's aggregate.
    /// </summary>
    /// <remarks>
    /// Every target passes <see cref="RollupRecompute.EnsureTenancyDoesNotCross"/> before it is returned, so a pair that
    /// disagrees about tenancy — a schema that did not come through the mapper — fails the plan instead of composing a
    /// subquery with no tenant predicate (Task 11 review m2).
    /// </remarks>
    /// <param name="current">The schema the store holds, its declared renames already applied.</param>
    /// <param name="desired">The schema the apply produces.</param>
    /// <exception cref="InvalidOperationException">A target's parent and child disagree about tenancy.</exception>
    internal static IReadOnlyList<Target> Targets(SchemaModel current, SchemaModel desired)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        IReadOnlyList<Target> targets = [.. from parent in desired.Entities
                                           let stored = Named(current, parent.Name)
                                           where stored is not null
                                           from field in parent.Fields
                                           where field.Rollup is not null && Changed(stored, field)
                                           let child = Named(desired, field.Rollup!.From)
                                           where child is not null
                                           select new Target(parent, field, child)];
        foreach (var target in targets)
        {
            RollupRecompute.EnsureTenancyDoesNotCross(target.Parent, target.Child);
        }

        return targets;
    }

    /// <summary>Every <see cref="Targets"/> as one recompute statement each.</summary>
    /// <param name="current">The schema the store holds, its declared renames already applied.</param>
    /// <param name="desired">The schema the apply produces.</param>
    /// <param name="dialect">This engine's table, column and "differs" spelling.</param>
    /// <param name="fields">This engine's field renderer, for the aggregate.</param>
    /// <param name="filter">The rendered filter of a rollup over its (desired) child, or <see langword="null"/> for none.</param>
    internal static IReadOnlyList<Statement> Statements(
        SchemaModel current, SchemaModel desired, IAlvoSqlDialect dialect, IFieldSqlRenderer fields,
        Func<EntitySchema, RollupSchema, string?> filter)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(filter);

        return [.. Targets(current, desired).Select(target => new Statement(
            target.Parent.Name, target.Field.Name, target.Child, Sql(target.Parent, target.Field, target.Child, dialect, fields, filter)))];
    }

    /// <summary>
    /// Whether <paramref name="field"/>'s rollup is not the one <paramref name="stored"/> declares under that name — the
    /// values the table holds for it are then not this definition's aggregate.
    /// </summary>
    /// <param name="stored">The current entity.</param>
    /// <param name="field">The desired field.</param>
    private static bool Changed(EntitySchema stored, FieldSchema field) =>
        stored.Fields.FirstOrDefault(candidate => string.Equals(candidate.Name, field.Name, StringComparison.Ordinal)) is not { } before
        || !Equals(before.Rollup, field.Rollup);

    private static string Sql(
        EntitySchema parent, FieldSchema field, EntitySchema child, IAlvoSqlDialect dialect, IFieldSqlRenderer fields,
        Func<EntitySchema, RollupSchema, string?> filter)
    {
        var column = dialect.RenderColumn(field.Name);
        var sub = Subquery(parent, field.Rollup!, child, dialect, fields, filter);
        return $"UPDATE {Table(parent, dialect)} SET {column} = {sub} WHERE {dialect.RenderDistinct(column, sub)}";
    }

    private static string Subquery(
        EntitySchema parent, RollupSchema rollup, EntitySchema child, IAlvoSqlDialect dialect, IFieldSqlRenderer fields,
        Func<EntitySchema, RollupSchema, string?> filter)
    {
        var alias = dialect.RenderColumn(ChildAlias);
        var parentId = $"{Table(parent, dialect)}.{dialect.RenderColumn(AlvoManagedColumns.Id)}";
        var where = filter(child, rollup) is { } rendered ? $" AND ({rendered})" : string.Empty;

        return $"(SELECT {RollupRecompute.Aggregate(child, rollup, fields)} FROM {Table(child, dialect)} AS {alias} "
            + $"WHERE {alias}.{dialect.RenderColumn(rollup.Via)} = {parentId}{TenantPredicate(parent, child, alias, dialect)}{where})";
    }

    // The write path's tenant narrowing, correlated: a scoped child counts into its own tenant's parent only. A pair that
    // disagrees about tenancy is refused at apply (RollupResolver) and again by Targets (the write path's belt), so it
    // never reaches here.
    private static string TenantPredicate(EntitySchema parent, EntitySchema child, string alias, IAlvoSqlDialect dialect)
    {
        if (child.Tenancy != TenancyMode.Scoped)
        {
            return string.Empty;
        }

        var tenant = dialect.RenderColumn(AlvoManagedColumns.TenantId);
        return $" AND {alias}.{tenant} = {Table(parent, dialect)}.{tenant}";
    }

    private static EntitySchema? Named(SchemaModel schema, string name) =>
        schema.Entities.FirstOrDefault(entity => string.Equals(entity.Name, name, StringComparison.Ordinal));

    private static string Table(EntitySchema entity, IAlvoSqlDialect dialect) => dialect.RenderTable(entity, lockedPreImageFor: null);
}
