using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A rollup's parent lock has to be issued through <b>whichever of the two positions</b> a dialect expresses row
/// locking in — and this is the only place that can be asserted, because neither shipped engine uses the second
/// one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bug this exists for was real and silent.</b> An earlier revision read only
/// <see cref="IAlvoSqlDialect.RowLockClause"/>. On PostgreSQL that is the lock, and on SQLite its emptiness
/// correctly means "issue no locking read at all" — so both in-repo drivers were right and the suite was green.
/// On a <em>table-hint</em> engine (T-SQL / Azure SQL, which has no trailing locking clause and spells the same
/// thing as <c>FROM t WITH (UPDLOCK, ROWLOCK)</c>) the clause is legitimately empty too, so the read was skipped
/// entirely and the parent was never locked — silently reproducing the measured 31-of-40 lost update on the one
/// engine §0 principle 3 names and no in-repo test covers.
/// </para>
/// <para>
/// It asserts the statement rather than the effect, deliberately: the effect needs a real engine of a kind this
/// repository does not ship, and the statement is exactly what a driver author gets wrong.
/// <c>TSqlSqlDialect</c> is the rehearsal the port keeps for that purpose.
/// </para>
/// </remarks>
public class RollupLockStatementTests
{
    /// <summary>
    /// PostgreSQL's shape: the lock is a trailing clause, so the statement carries it and the table source is
    /// the plain one.
    /// </summary>
    [Fact]
    public void A_trailing_clause_dialect_locks_in_the_clause()
    {
        var sql = Statement(new TestSqlDialect()).ShouldNotBeNull();

        sql.ShouldContain("FOR TEST");
        sql.ShouldStartWith("SELECT ");
    }

    /// <summary>
    /// T-SQL's shape, and the regression this file exists for: the lock is a <b>table hint</b>, and the statement
    /// must still be issued and must still carry it.
    /// </summary>
    [Fact]
    public void A_table_hint_dialect_locks_in_the_from_clause()
    {
        var dialect = new TSqlSqlDialect();
        var expected = dialect.RenderTable(Parent, PreImageMutation.Update);

        var sql = Statement(dialect).ShouldNotBeNull("a table-hint engine still needs the locking read issued");

        sql.ShouldContain(expected);
        sql.ShouldContain("UPDLOCK", Case.Insensitive);
    }

    /// <summary>
    /// SQLite's shape: no lock in either position, so <b>no statement at all</b> — reading the parent before
    /// writing inside a deferred transaction killed 12 of 24 writers on <c>SQLITE_BUSY_SNAPSHOT</c>, so an
    /// unlocked read here would be worse than none.
    /// </summary>
    [Fact]
    public void A_dialect_that_locks_in_neither_position_issues_no_read()
        => Statement(new UnlockableSqlDialect()).ShouldBeNull();

    private static string? Statement(IAlvoSqlDialect dialect) =>
        new RollupRecompute(dialect, new TestFieldSqlRenderer()).LockStatement(Parent);

    private static EntitySchema Parent => new()
    {
        Name = "invoices",
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

    /// <summary>
    /// A dialect with no row lock in either position — SQLite's answer, spelled here because
    /// <c>MMLib.Alvo.Data.Sqlite</c> is not referenced from this project and <see cref="TestSqlDialect"/>
    /// deliberately answers a clause.
    /// </summary>
    private sealed class UnlockableSqlDialect : IAlvoSqlDialect
    {
        public string RowLockClause(PreImageMutation mutation) => string.Empty;

        public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor) =>
            AlvoSqlIdentifier.Quote(entity!.Name);

        public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

        public string RenderNullProjection(string storeType) => $"CAST(NULL AS {storeType})";

        public SqlConstraintViolation? DecodeConstraintViolation(DbException failure) => null;
    }
}
