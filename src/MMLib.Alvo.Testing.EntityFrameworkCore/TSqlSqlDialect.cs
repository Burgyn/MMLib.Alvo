using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// A T-SQL-shaped <see cref="IAlvoSqlDialect"/>, standing in for the SQL Server / Azure SQL driver
/// §0 principle 3 requires the core to support — the statement-shape counterpart of
/// <see cref="TSqlFieldSqlRenderer"/>, which does the same job for expression shape.
/// </summary>
/// <remarks>
/// <para>
/// It exists because T-SQL is the engine that cannot express one member the way both shipped drivers
/// do. PostgreSQL locks a pre-image read with a <b>trailing clause</b> (<c>FOR NO KEY UPDATE</c>) and
/// SQLite has no clause at all, so a seam offering only a trailing clause looks sufficient. T-SQL locks
/// with a <b>table hint inside the <c>FROM</c></b> (<c>FROM notes WITH (UPDLOCK, ROWLOCK)</c>) and has no
/// trailing equivalent — and because <see cref="string.Empty"/> is a <em>legitimate</em> answer from
/// <see cref="IAlvoSqlDialect.RowLockClause"/> (it is SQLite's), a T-SQL driver author following the
/// contract would have shipped silently unlocked <c>WITH CHECK</c> pre-images: a real
/// time-of-check/time-of-use race on an engine whose default isolation level is READ COMMITTED, and
/// indistinguishable from correct SQLite behaviour.
/// </para>
/// <para>
/// So this fake is the proof that the seam is sufficient rather than merely documented: it emits the hint
/// from <see cref="RenderTable"/>, which is the member rendering at the position T-SQL's grammar requires,
/// and returns the empty string from <see cref="RowLockClause"/> <em>honestly</em> — the two answers
/// together mean "this engine locks, in the other position", which is what the seam has to be able to
/// say. Neither the structural composer nor either shipped driver needed a change to accommodate it.
/// </para>
/// <para>
/// Public and shipped here rather than declared in one test project, for the same reason
/// <see cref="TSqlFieldSqlRenderer"/> is: the seam is exercised by the EF data path's composer tests
/// <em>and</em> by <see cref="AlvoSqlDialectContractTests"/>, and two copies of the fake are how the two
/// would come to be proved against different T-SQL.
/// </para>
/// </remarks>
public sealed class TSqlSqlDialect : IAlvoSqlDialect
{
    /// <summary>
    /// The hint an <c>UPDATE</c>'s pre-image takes: <c>UPDLOCK</c> holds an update lock until the
    /// transaction commits, and <c>ROWLOCK</c> keeps the granularity at the row so the lock does not
    /// escalate to the page or the table.
    /// </summary>
    private const string UpdateHint = "WITH (UPDLOCK, ROWLOCK)";

    /// <summary>
    /// A <c>DELETE</c>'s pre-image additionally takes <c>HOLDLOCK</c> — serializable range semantics, so
    /// the key this statement is about to remove cannot be re-inserted or referenced concurrently. It is
    /// T-SQL's answer to the same question PostgreSQL answers with <c>FOR UPDATE</c> rather than
    /// <c>FOR NO KEY UPDATE</c>.
    /// </summary>
    private const string DeleteHint = "WITH (UPDLOCK, ROWLOCK, HOLDLOCK)";

    /// <inheritdoc/>
    /// <remarks>
    /// The hint is appended to the table source, which is where T-SQL's grammar puts it. A read that is not
    /// a pre-image takes no hint at all: an ordinary <c>list</c> or <c>get</c> must not hold an update lock
    /// for the rest of the transaction.
    /// </remarks>
    public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var table = $"[{entity.Name.Replace("]", "]]", StringComparison.Ordinal)}]";

        return lockedPreImageFor switch
        {
            PreImageMutation.Update => $"{table} {UpdateHint}",
            PreImageMutation.Delete => $"{table} {DeleteHint}",
            _ => table,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Empty, and honestly so: T-SQL has no trailing locking clause, and this dialect has already taken the
    /// lock in the <c>FROM</c>. Answering anything here would be a syntax error in the one statement a
    /// <c>WITH CHECK</c> verdict is based on.
    /// </remarks>
    public string RowLockClause(PreImageMutation mutation) => string.Empty;

    /// <inheritdoc/>
    public string RenderColumn(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        return $"[{columnName.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    /// <inheritdoc/>
    public string RenderNullProjection(string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        return $"CAST(NULL AS {storeType})";
    }

    /// <summary>
    /// T-SQL spells the whole window as one clause, offset first: <c>OFFSET &lt;m&gt; ROWS FETCH NEXT
    /// &lt;n&gt; ROWS ONLY</c> — <c>OFFSET</c> is not optional there even with no caller-supplied offset,
    /// which is exactly why this member takes both markers together rather than being split across two
    /// members the way an earlier revision of this port shaped it. That split let this fake answer the
    /// limit half correctly (hard-coding <c>OFFSET 0 ROWS</c> when no real offset existed) while the *pair*
    /// would have been wrong the moment a real offset arrived — two conflicting <c>OFFSET</c> clauses in
    /// one statement, a silently wrong page. One member that sees both values at once makes that
    /// unrepresentable: with no offset it renders the same literal zero the old split version hard-coded,
    /// and with one it renders the caller's real value instead.
    /// </summary>
    /// <param name="rowCountParameterMarker">The bind-parameter reference holding the row count.</param>
    /// <param name="rowOffsetParameterMarker">
    /// The bind-parameter reference holding the number of rows to skip, or <see langword="null"/> for none —
    /// rendered as the literal <c>0</c> rather than omitted, because T-SQL's <c>FETCH</c> cannot appear
    /// without a preceding <c>OFFSET</c>. The literal is safe to format directly: it is a framework
    /// constant standing in for "no offset was asked for," never caller-supplied text.
    /// </param>
    public string RowWindowClause(string rowCountParameterMarker, string? rowOffsetParameterMarker = null)
    {
        ArgumentNullException.ThrowIfNull(rowCountParameterMarker);
        var offset = rowOffsetParameterMarker ?? "0";
        return $"OFFSET {offset} ROWS FETCH NEXT {rowCountParameterMarker} ROWS ONLY";
    }
}
