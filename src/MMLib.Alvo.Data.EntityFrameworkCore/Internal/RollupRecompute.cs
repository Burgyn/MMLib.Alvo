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
/// admits one writer at a time. So a dialect that expresses no lock in <em>either</em> of the port's two
/// positions means "issue no locking read here", not "issue an unlocked one", and the read is skipped entirely —
/// see <see cref="LockStatement"/>, which is careful to ask about both positions rather than only the trailing
/// clause.
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
/// <b>What this does not claim.</b> Three gaps, all decisions rather than oversights, and all named here
/// because each of them is a number that stays stale while looking like data:
/// </para>
/// <para>
/// 1. <b>An out-of-band write is not seen.</b> The recompute is unbypassable only for writes that go through
/// this port; a direct <c>INSERT</c> into the child table by another application leaves the rollup stale. That
/// is the honest difference from <c>computed</c>, whose value the engine itself maintains.
/// </para>
/// <para>
/// 2. <b>A rollup <em>over</em> a rollup does not propagate.</b> Only entities rolling up the child that was
/// written are recomputed. If <c>C</c> rolls up into <c>B</c> and <c>B</c> into <c>A</c>, a write to <c>C</c>
/// moves <c>B</c>'s aggregate by a raw <c>UPDATE</c> that never re-enters this port, so <c>A</c>'s does not
/// move. Deliberate: the sources' own worked ladder (<c>baas-analyza:1358</c>) nests <c>computed</c> over a
/// rollup — which <em>does</em> work, because the engine maintains a generated column — and never nests one
/// rollup inside another. Closing it means a transitive walk with cycle detection, which is a feature with its
/// own issue, not a line here. A descriptor that declares one gets a correct <c>B</c> and a stale <c>A</c>.
/// </para>
/// <para>
/// 3. <b>The parent's change emits no event.</b> The write's outbox row is the <em>child</em>'s; the parent's
/// <c>UPDATE</c> is a raw statement that never reaches the change tracker, so an automation conditioned on
/// <c>entity.&lt;parent&gt;.updated</c> does not fire for a rollup-only change. Emitting one would mean a second
/// event per child write whose <c>old</c>/<c>new</c> images this layer does not have in hand, so it is left to
/// whoever needs it.
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
    /// Takes the parent row's write lock, or issues nothing at all when this engine expresses no lock in
    /// <b>either</b> of the two positions the port defines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both positions are asked for, and asking only one of them was a real bug.</b>
    /// <see cref="IAlvoSqlDialect"/>'s contract is that a locking read carries its lock in exactly one place:
    /// a trailing <see cref="IAlvoSqlDialect.RowLockClause"/> (PostgreSQL's <c>FOR NO KEY UPDATE</c>) or a
    /// table hint from <see cref="IAlvoSqlDialect.RenderTable"/> (T-SQL's
    /// <c>FROM notes WITH (UPDLOCK, ROWLOCK)</c>, which has no trailing form at all). A version of this method
    /// that only read the trailing clause would issue <em>no lock</em> on a table-hint engine — and, because
    /// that engine's <see cref="IAlvoSqlDialect.RowLockClause"/> is legitimately empty, it would skip the read
    /// entirely and silently reproduce the measured 31-of-40 lost update on the one engine §0 principle 3 names
    /// and no in-repo driver covers. So the table source is rendered <em>for this mutation</em>, exactly as
    /// <c>ReadStatementComposer</c> renders a pre-image read's.
    /// </para>
    /// <para>
    /// <b>And "no lock anywhere" is a real answer, not a gap.</b> SQLite expresses row locking in neither
    /// position, and it must <em>not</em> read the parent before writing inside a deferred transaction — 12 of
    /// 24 writers died on <c>SQLITE_BUSY_SNAPSHOT</c> when they did. The two cases are told apart by the
    /// pairing the port already defines: a dialect that hints its lock returns a <em>different</em> table
    /// source for a locked pre-image than for a plain read, so comparing the two is the same question the
    /// contract suite asks, rather than a new one invented here.
    /// </para>
    /// <para>
    /// The <see cref="PreImageMutation.Update"/> mode is deliberate rather than incidental. The recompute
    /// provably never touches the parent's key, which is exactly the case PostgreSQL documents
    /// <c>FOR NO KEY UPDATE</c> for, and that weaker mode does not block the <c>FOR KEY SHARE</c> another
    /// table's foreign-key check takes. It still conflicts with itself, so two concurrent recomputes of one
    /// parent serialise — which is the entire correctness argument. Asking for
    /// <see cref="PreImageMutation.Delete"/> to obtain the literal words <c>FOR UPDATE</c> would serialise
    /// unrelated inserts against the parent for no benefit.
    /// </para>
    /// </remarks>
    private Task LockAsync(AlvoDataContext db, Group group, CancellationToken cancellationToken) =>
        LockStatement(group.Parent) is { } sql
            ? db.Database.ExecuteSqlRawAsync(sql, [group.ParentId], cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    /// The locking read for one parent entity, or <see langword="null"/> when this engine expresses no row lock
    /// in either position and the read must therefore not be issued at all.
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> and separated from its execution so it can be asserted without a database:
    /// the obligation it carries is about the <b>third</b> engine — one whose locking grammar is a table hint —
    /// and no in-repo driver has that grammar, so a fact over the two shipped dialects cannot see a regression
    /// here. <c>MMLib.Alvo.Testing.Data.TSqlSqlDialect</c> is what pins it.
    /// </remarks>
    /// <param name="parent">The entity holding the rollup fields.</param>
    internal string? LockStatement(EntitySchema parent)
    {
        var clause = dialect.RowLockClause(PreImageMutation.Update);
        var locked = dialect.RenderTable(parent, PreImageMutation.Update);

        if (clause.Length == 0 && string.Equals(locked, Table(parent), StringComparison.Ordinal))
        {
            return null;
        }

        var id = dialect.RenderColumn(AlvoManagedColumns.Id);

        return $"SELECT {id} FROM {locked} WHERE {id} = {{0}} {clause}".TrimEnd();
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
