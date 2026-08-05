using Microsoft.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Recomputes every <c>rollup</c> a write to a child entity can change, <b>inside that write's own
/// transaction</b>: the parent's row lock first, then one
/// <c>UPDATE parent SET &lt;rollup&gt; = (SELECT &lt;op&gt;(&lt;field&gt;) FROM &lt;child&gt; WHERE &lt;fk&gt; = @parent)</c>
/// per parent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the lock comes first, and why the single-statement recompute is NOT enough on its own.</b> Measured
/// on PostgreSQL, READ COMMITTED, 40 concurrent writers against one parent: the atomic
/// <c>UPDATE parent SET total = (SELECT SUM …)</c> wrote <b>31</b> of 40 once a 50 ms delay widened the window.
/// Under READ COMMITTED the <c>SET</c> expression is evaluated from the snapshot taken at statement start, and
/// when the row lock is finally granted EvalPlanQual re-checks only the outer <c>WHERE</c> (<c>id = @p</c>,
/// still true) — so the stale value is written. This is the same EvalPlanQual mechanism that bit the outbox
/// claim in PR5a; second occurrence in this codebase. Taking the row lock <em>before</em> the recompute makes
/// the following statement take a fresh snapshot, and the same run then wrote 40 of 40.
/// </para>
/// <para>
/// <b>Why the lock is the dialect's, and is a no-op on SQLite.</b> The two engines pull in opposite
/// directions. PostgreSQL requires the lock; SQLite must <em>not</em> read the parent before writing inside a
/// deferred transaction — 12 of 24 writers died on <c>SQLITE_BUSY_SNAPSHOT</c> (<c>[5/517]</c>) when they did —
/// and needs no lock at all, because the child write already took the database-wide write lock and SQLite
/// admits one writer at a time. So an empty <see cref="IAlvoSqlDialect.RowLockClause"/> means "issue no locking
/// read here", not "issue an unlocked one", and the read is skipped entirely.
/// </para>
/// <para>
/// <b>Why the lock is taken after the child write rather than before it, which is where the design's numbering
/// puts it.</b> What the measurement establishes is lock-before-<em>recompute</em>; where the child write sits
/// is free. Taking it here buys two things: the lock is held for less time, and every parent this write touches
/// is locked in one place, <b>in id order</b> — which is what stops two writers that move children between the
/// same two parents in opposite directions from deadlocking. Locking before the child write would spread the
/// acquisition over three call sites with no ordering guarantee between them.
/// </para>
/// <para>
/// <b>Why all five ops go through one recompute.</b> A <c>total = total + delta</c> shortcut is commutative for
/// <c>sum</c> and <c>count</c> only, drifts with no self-correction if a single write is ever missed, and is
/// simply wrong for <c>min</c>/<c>max</c>, where removing the extreme child cannot be expressed as a delta at
/// all.
/// </para>
/// <para>
/// <b>Why the aggregated column goes through the dialect's comparison repair.</b> <c>MIN</c>/<c>MAX</c> compare,
/// and SQLite stores a <c>decimal</c> as <c>TEXT</c> — so <c>MIN('10.0', '6.0')</c> answers <c>'10.0'</c>
/// lexicographically. <see cref="IFieldSqlRenderer.RenderComparableOperands"/> is the member that already owns
/// that repair (its own remarks call it an ordering key, which is exactly what an extreme-value aggregate is),
/// so it is reused rather than a second decimal rule being invented here. It is applied for every op, not only
/// the comparing ones: one code path, and the repair is a no-op wherever the storage already orders correctly.
/// </para>
/// <para>
/// <b>What this does not claim.</b> The recompute is unbypassable only for writes that go through this port. A
/// direct <c>INSERT</c> into the child table by another application leaves the rollup stale — the honest
/// difference from <c>computed</c>, whose value the engine itself maintains. Named here rather than discovered.
/// </para>
/// </remarks>
/// <param name="dialect">This driver's statement seam: the table source, the delimiters and the row lock.</param>
/// <param name="fields">This driver's expression seam, for the aggregated column and its value repair.</param>
internal sealed class RollupRecompute(IAlvoSqlDialect dialect, IFieldSqlRenderer fields)
{
    /// <summary>
    /// Recomputes every rollup the images of one child row can have changed.
    /// </summary>
    /// <param name="db">The context whose transaction the statements join.</param>
    /// <param name="child">The child entity that was written.</param>
    /// <param name="images">
    /// The child row's images — the post-image of a create, both images of an update, the pre-image of a
    /// delete. <b>Both</b> images matter on an update because the foreign key is writable: moving a child from
    /// one parent to another changes two aggregates, and the design does not name that case.
    /// </param>
    /// <param name="cancellationToken">The caller's token.</param>
    internal async Task ForChildWriteAsync(
        AlvoDataContext db,
        EntitySchema child,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> images,
        CancellationToken cancellationToken)
    {
        foreach (var group in Groups(db.AppliedSchema, child, images))
        {
            await LockAsync(db, group, cancellationToken).ConfigureAwait(false);
            await RecomputeAsync(db, child, group, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether any entity in <paramref name="schema"/> rolls up <paramref name="child"/> at all.</summary>
    /// <remarks>
    /// The fast path every write on an entity nothing rolls up takes, which is nearly all of them: without it
    /// each of the four write sites would walk the whole applied schema per call.
    /// </remarks>
    internal static bool IsRolledUp(SchemaModel schema, EntitySchema child) =>
        schema.Entities.Any(parent => parent.Fields.Any(field => RollsUp(field, child)));

    private static bool RollsUp(FieldSchema field, EntitySchema child) =>
        field.Rollup is { } rollup && string.Equals(rollup.From, child.Name, StringComparison.Ordinal);

    /// <summary>
    /// One unit of work: the parent row, the foreign key its aggregates follow, and every rollup field that
    /// follows it — <b>ordered by parent id</b>.
    /// </summary>
    /// <param name="Parent">The entity holding the rollup fields.</param>
    /// <param name="ParentId">The row whose aggregates are being recomputed.</param>
    /// <param name="Via">The child's foreign-key column this group's aggregates filter on.</param>
    /// <param name="Fields">The rollup fields recomputed in one statement.</param>
    private sealed record Group(EntitySchema Parent, Guid ParentId, string Via, IReadOnlyList<FieldSchema> Fields);

    /// <summary>
    /// Every group one child write touches, in a <b>deterministic order</b>.
    /// </summary>
    /// <remarks>
    /// The order is the deadlock argument: two transactions that lock the same set of parent rows in the same
    /// order cannot deadlock on them, and an update that moves a child between two parents locks two. Ordering
    /// by the row id rather than by declaration order is what makes it total across processes.
    /// </remarks>
    private static IEnumerable<Group> Groups(
        SchemaModel schema, EntitySchema child, IReadOnlyList<IReadOnlyDictionary<string, object?>> images) =>
        from parent in schema.Entities
        let rollups = parent.Fields.Where(field => RollsUp(field, child)).ToList()
        where rollups.Count > 0
        from via in rollups.Select(field => field.Rollup!.Via).Distinct(StringComparer.Ordinal)
        from parentId in ParentIds(images, via)
        orderby parentId
        select new Group(parent, parentId, via, [.. rollups.Where(field => string.Equals(field.Rollup!.Via, via, StringComparison.Ordinal))]);

    /// <summary>The distinct parent ids <paramref name="images"/> name through <paramref name="via"/>.</summary>
    /// <remarks>
    /// Distinct, because an update that did not touch the foreign key names the same parent twice — and
    /// recomputing it twice is harmless but locking it twice is a second round trip for nothing. A
    /// <see langword="null"/> foreign key yields no parent: a nullable <c>ref</c> is a child that belongs to no
    /// one, and there is nothing to aggregate it into.
    /// </remarks>
    private static IEnumerable<Guid> ParentIds(IReadOnlyList<IReadOnlyDictionary<string, object?>> images, string via) =>
        images
            .Select(image => image.GetValueOrDefault(via))
            .OfType<Guid>()
            .Distinct();

    /// <summary>
    /// Takes the parent row's write lock, or issues nothing at all when this engine has no locking clause.
    /// </summary>
    /// <remarks>
    /// The <see cref="PreImageMutation.Update"/> mode is deliberate rather than incidental. The recompute
    /// provably never touches the parent's key, which is exactly the case PostgreSQL documents
    /// <c>FOR NO KEY UPDATE</c> for, and that weaker mode does not block the <c>FOR KEY SHARE</c> another
    /// table's foreign-key check takes. It still conflicts with itself, so two concurrent recomputes of one
    /// parent serialise — which is the entire correctness argument. Asking for
    /// <see cref="PreImageMutation.Delete"/> to obtain the literal words <c>FOR UPDATE</c> would serialise
    /// unrelated inserts against the parent for no benefit.
    /// </remarks>
    private async Task LockAsync(AlvoDataContext db, Group group, CancellationToken cancellationToken)
    {
        if (dialect.RowLockClause(PreImageMutation.Update) is not { Length: > 0 } clause)
        {
            return;
        }

        var id = dialect.RenderColumn(AlvoManagedColumns.Id);
        var sql = $"SELECT {id} FROM {Table(group.Parent)} WHERE {id} = {{0}} {clause}";

        await db.Database.ExecuteSqlRawAsync(sql, [group.ParentId], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One statement recomputing every rollup in <paramref name="group"/> from scratch.
    /// </summary>
    /// <remarks>
    /// One statement per group rather than per field, because the aggregates of one parent row are one fact
    /// about it: written separately, a reader between the two <c>UPDATE</c>s would see a <c>count</c> that does
    /// not match its <c>sum</c>. The <c>WHERE</c> narrows by row id alone — the id came off the child row this
    /// caller was already authorised to write, and a uuid primary key identifies one row, so no tenant
    /// predicate can change which row this is.
    /// </remarks>
    private async Task RecomputeAsync(
        AlvoDataContext db, EntitySchema child, Group group, CancellationToken cancellationToken)
    {
        var setters = string.Join(", ", group.Fields.Select(field => Setter(child, field)));
        var id = dialect.RenderColumn(AlvoManagedColumns.Id);
        var sql = $"UPDATE {Table(group.Parent)} SET {setters} WHERE {id} = {{0}}";

        await db.Database.ExecuteSqlRawAsync(sql, [group.ParentId], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One <c>&lt;rollup&gt; = (SELECT &lt;op&gt;(…) FROM &lt;child&gt; WHERE &lt;fk&gt; = @parent)</c>.</summary>
    /// <remarks>
    /// The subquery's own answer is stored as it comes: <c>0</c> for a <c>count</c> over no children and
    /// <c>NULL</c> for the other four. That is the engine's empty answer rather than one this layer invented, and
    /// a <c>COALESCE(…, 0)</c> here would make "no children yet" indistinguishable from "children summing to
    /// zero" on a field an author declared nullable.
    /// </remarks>
    private string Setter(EntitySchema child, FieldSchema field)
    {
        var rollup = field.Rollup!;
        var fk = dialect.RenderColumn(rollup.Via);
        var aggregate = Aggregate(child, rollup);

        return $"{dialect.RenderColumn(field.Name)} = (SELECT {aggregate} FROM {Table(child)} WHERE {fk} = {{0}})";
    }

    /// <summary>The aggregate expression, with the driver's value repair around the aggregated column.</summary>
    /// <remarks>
    /// <c>count</c> aggregates <em>rows</em>, so it takes no column and needs no repair — and it is
    /// <c>COUNT(*)</c> rather than <c>COUNT(&lt;fk&gt;)</c> so a nullable column can never make it undercount.
    /// The other four aggregate a value, and the repair is what stops <c>MIN</c>/<c>MAX</c> comparing a SQLite
    /// <c>decimal</c> as text.
    /// </remarks>
    private string Aggregate(EntitySchema child, RollupSchema rollup)
    {
        if (rollup.Op == RollupOperation.Count)
        {
            return "COUNT(*)";
        }

        var column = fields.RenderField(child, rollup.Field!);
        var declared = child.Fields.First(candidate => string.Equals(candidate.Name, rollup.Field, StringComparison.Ordinal));
        var (repaired, _) = fields.RenderComparableOperands(column, column, CelFieldType.Of(declared));

        return $"{Function(rollup.Op)}({repaired})";
    }

    /// <summary>
    /// The SQL aggregate function for one operation. Exhaustive by construction: an unmapped member throws
    /// rather than defaulting, because a defaulted aggregate is a wrong number nothing reports.
    /// </summary>
    private static string Function(RollupOperation op) => op switch
    {
        RollupOperation.Sum => "SUM",
        RollupOperation.Avg => "AVG",
        RollupOperation.Min => "MIN",
        RollupOperation.Max => "MAX",
        RollupOperation.Count => "COUNT",
        _ => throw new ArgumentOutOfRangeException(
            nameof(op), op, "Unmapped rollup operation; give it a SQL aggregate function here."),
    };

    /// <summary>
    /// The table source, asked for with no lock: the locking read is a statement of its own, and a dialect whose
    /// locking grammar is a table hint must not have it repeated on the recompute's own <c>UPDATE</c>.
    /// </summary>
    private string Table(EntitySchema entity) => dialect.RenderTable(entity, lockedPreImageFor: null);
}
