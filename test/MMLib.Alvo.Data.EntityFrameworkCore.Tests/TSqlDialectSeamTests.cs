using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Testing;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The seam-sufficiency rehearsal for the one engine §0 principle 3 names and Alvo does not ship a driver
/// for: SQL Server / Azure SQL. <see cref="TSqlFieldSqlRenderer"/> already proves the expression seam is
/// sufficient for T-SQL; these facts do the same for the statement seam, composing a real read through the
/// production composer with nothing but the two T-SQL fakes registered.
/// </summary>
/// <remarks>
/// The member that made this necessary is the row lock. T-SQL expresses it as a table hint inside the
/// <c>FROM</c> and has no trailing equivalent, while <see cref="string.Empty"/> is a legitimate answer from
/// <see cref="IAlvoSqlDialect.RowLockClause"/> — so a seam offering only the trailing position lets a T-SQL
/// driver look correct while shipping unlocked <c>WITH CHECK</c> pre-images.
/// </remarks>
public class TSqlDialectSeamTests
{
    /// <summary>
    /// The whole point: a dialect whose grammar puts the lock in the <c>FROM</c> can put it there, because
    /// the member rendering at that position is told the read is a locking pre-image and which mutation it
    /// precedes.
    /// </summary>
    [Theory]
    [InlineData(PreImageMutation.Update, "WITH (UPDLOCK, ROWLOCK)")]
    [InlineData(PreImageMutation.Delete, "WITH (UPDLOCK, ROWLOCK, HOLDLOCK)")]
    public void A_t_sql_pre_image_read_takes_its_lock_as_a_table_hint_in_the_from(
        PreImageMutation mutation, string hint)
    {
        var sql = Compose(new ReadStatementComposer.ReadStatementOptions
        {
            RowId = Guid.NewGuid(),
            LockFor = mutation,
        });

        sql.ShouldContain($" FROM [vehicle] {hint} WHERE ");
    }

    /// <summary>
    /// And it stays honest at the other position: the trailing clause is empty because the lock is already
    /// taken, not because the engine takes none. A T-SQL suffix meaning <c>FOR NO KEY UPDATE</c> does not
    /// exist, so anything appended here would be a syntax error in the one statement a <c>WITH CHECK</c>
    /// verdict is based on.
    /// </summary>
    [Fact]
    public void The_t_sql_dialect_appends_no_trailing_locking_clause()
        => Compose(new ReadStatementComposer.ReadStatementOptions
        {
            RowId = Guid.NewGuid(),
            LockFor = PreImageMutation.Update,
        }).ShouldEndWith("AND ([id] = @alvo_id)");

    /// <summary>
    /// The non-vacuity control. An ordinary <c>list</c> must not take an update lock it holds for the rest of
    /// the transaction, so the hint has to be absent when the read is not a pre-image — otherwise the fact
    /// above would also pass for a dialect that hinted every table source it ever rendered.
    /// </summary>
    [Fact]
    public void A_read_that_is_not_a_pre_image_takes_no_hint_at_all()
        => Compose(new ReadStatementComposer.ReadStatementOptions())
            .ShouldContain(" FROM [vehicle] WHERE ");

    /// <summary>
    /// T-SQL's truncation grammar differs too (<c>OFFSET … FETCH NEXT</c>, whose <c>OFFSET</c> is not
    /// optional), which is why the whole window is a default interface member rather than a shape baked
    /// into the composer. Asserted here so the second T-SQL divergence is rehearsed alongside the first.
    /// </summary>
    [Fact]
    public void A_t_sql_page_truncates_with_offset_fetch_rather_than_limit()
        => Compose(new ReadStatementComposer.ReadStatementOptions { Limit = 5 })
            .ShouldEndWith(" ORDER BY [id] OFFSET 0 ROWS FETCH NEXT @alvo_limit ROWS ONLY");

    /// <summary>
    /// The third T-SQL divergence, and the one that used to be unrepresentable: with a real caller offset
    /// as well as a limit, the fused window names <b>both</b> markers in one clause rather than emitting a
    /// second, conflicting <c>OFFSET</c> — the defect <c>RowWindowClause</c> exists to make impossible. Before
    /// the fix, <c>RowLimitClause</c> alone had no way to see this offset and would have hard-coded
    /// <c>OFFSET 0 ROWS</c> regardless, producing a silently wrong page rather than this one.
    /// </summary>
    [Fact]
    public void A_t_sql_page_with_both_a_limit_and_an_offset_renders_exactly_one_offset_clause()
        => Compose(new ReadStatementComposer.ReadStatementOptions { Limit = 5, Offset = 3 })
            .ShouldEndWith(" ORDER BY [id] OFFSET @alvo_offset ROWS FETCH NEXT @alvo_limit ROWS ONLY");

    private static string Compose(ReadStatementComposer.ReadStatementOptions options)
    {
        using var services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();
        var composer = new ReadStatementComposer(
            services.GetRequiredService<IPredicateRenderer>(), new TSqlFieldSqlRenderer(), new TSqlSqlDialect());
        var decision = SnapshotFixture.Decision(
            services, SnapshotFixture.VehicleWith(list: "owner_id == @user.id"), DataOperation.List);

        return composer.Compose(
            AlvoDataFixtures.Vehicle,
            decision,
            AlvoDataFixtures.Caller,
            options,
            ReadModelFixture.Rows(AlvoDataFixtures.Vehicle)).Sql;
    }
}
