using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Text.RegularExpressions;

// EF1001 matches on a namespace ending in ".Internal", so here it flags Alvo's OWN internals — this project
// is granted them by InternalsVisibleTo — rather than an Entity Framework internal API.
#pragma warning disable EF1001

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The claim statement's shape, with no database in sight: the four properties that decide whether the queue
/// is correct at all, each of which a passing behavioural suite on one engine can hide.
/// </summary>
/// <remarks>
/// Shape facts rather than behaviour, because these are the ones that survive in a project that cannot open a
/// connection — and because three of the four are about what the statement must <em>not</em> say, which no
/// amount of green behaviour proves. The behavioural half lives on the engines:
/// <c>OutboxStoreContractTests</c> for the protocol and
/// <c>PostgreSqlOutboxStoreTests.A_second_claimant_claims_nothing_rather_than_the_same_rows</c> for the one
/// property a shape assertion genuinely cannot see.
/// </remarks>
public class OutboxClaimSqlTests
{
    /// <summary>
    /// The claim filters the dispatch flag. A high-water mark on a monotonic key drops a row silently, because
    /// ids are minted per process and a sequence commits out of order.
    /// </summary>
    [Fact]
    public void The_claim_filters_the_dispatch_flag_and_never_a_high_water_mark()
    {
        var sql = OutboxTable.ClaimSql(TableName);

        sql.ShouldContain("dispatched_at IS NULL");
        sql.ShouldNotContain(
            ">", Case.Sensitive, "a high-water mark on a monotonic key drops a row silently");
    }

    /// <summary>
    /// The <c>ORDER BY</c> and the <c>LIMIT</c> are in the subquery, because neither engine allows either
    /// inside an <c>UPDATE</c>.
    /// </summary>
    /// <remarks>
    /// Measured on <b>both</b> engines (spike Q3), which corrects the risk register twice: the parser dies on
    /// <c>ORDER</c> rather than on <c>limit</c> — SQLite <c>'near "ORDER": syntax error'</c>, PostgreSQL
    /// <c>42601 syntax error at or near "ORDER"</c> — and PostgreSQL refuses the same statement, so this is
    /// portability rather than a SQLite workaround. The bundled <c>e_sqlite3</c> also reports
    /// <c>SQLITE_ENABLE_UPDATE_DELETE_LIMIT</c> unset, so the flag is not an escape either.
    /// </remarks>
    [Fact]
    public void The_order_by_and_limit_are_in_the_subquery_because_neither_engine_allows_them_in_update()
    {
        var sql = OutboxTable.ClaimSql(TableName);

        sql.ShouldContain($"id IN (SELECT id FROM {TableName}");
        sql.ShouldContain("ORDER BY id");
        sql.ShouldContain("LIMIT @batch");
        Regex.IsMatch(sql, $@"UPDATE\s+{TableName}[\s\S]*?LIMIT", RegexOptions.None, _matchTimeout)
            .ShouldBeTrue("the only LIMIT must be the subquery's");
    }

    /// <summary>
    /// There is no row-lock hint. <c>SKIP LOCKED</c> skips the row rather than the key, so it delivers neither
    /// global nor per-entity-key ordering, and with exactly one dispatcher it buys nothing at all.
    /// </summary>
    [Fact]
    public void The_claim_takes_no_row_lock_hint_because_one_dispatcher_needs_none()
    {
        var sql = OutboxTable.ClaimSql(TableName);

        sql.ShouldNotContain("SKIP LOCKED");
        sql.ShouldNotContain("FOR UPDATE");
    }

    /// <summary>
    /// Spike Q4: the outer <c>WHERE</c> must repeat the subquery's claimability predicate, or two claimants
    /// deliver every row twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under <c>READ COMMITTED</c>, PostgreSQL's EvalPlanQual re-check re-evaluates the <b>outer</b>
    /// <c>WHERE</c> against the row the winner just updated — and nothing else. A subquery-only predicate is
    /// not part of that re-check, so the loser's <c>id IN (…)</c> still holds and it re-claims rows that are
    /// already claimed: measured as <em>"A claimed 10, B claimed 10, overlap 10; rows with attempts &gt; 1:
    /// 10"</em>. This fact is a shape assertion rather than a behaviour one because it is the one that
    /// survives in a project with no database; the behaviour is pinned on PostgreSQL by
    /// <c>PostgreSqlOutboxStoreTests.A_second_claimant_claims_nothing_rather_than_the_same_rows</c>.
    /// </para>
    /// <para>
    /// <b>The split is asserted to have found a separator before the prefix is read, and that is the whole
    /// difference between this fact and a vacuous one.</b> Deleting the outer predicates also deletes the
    /// <c>AND</c> in front of the subquery, so a plain <c>Split(…)[0]</c> hands back the <em>entire</em>
    /// statement — whose subquery still carries both predicates — and the fact passes over exactly the
    /// mutation it exists to catch. Measured: with the outer <c>WHERE</c> reduced to <c>id IN (subquery)</c>,
    /// the prefix-only version stayed green while PostgreSQL's two-claimant fact went red.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_outer_where_repeats_the_claimability_predicate_it_is_not_redundant()
    {
        var clauses = OutboxTable.ClaimSql(TableName).Split(OuterWhereSeparator);

        clauses.Length.ShouldBe(
            2, $"the subquery must be one more '{OuterWhereSeparator}' on a non-empty outer WHERE");
        clauses[0].ShouldContain("dispatched_at IS NULL");
        clauses[0].ShouldContain("claimed_at IS NULL");
        clauses[0].Contains("attempts < @max_attempts", StringComparison.Ordinal).ShouldBeTrue(
            "the attempt ceiling is subject to the same EvalPlanQual re-check as claimability, so an outer WHERE "
            + "that omits it lets a claimant push an entry past the bound. This assertion is the reason the "
            + "omission could survive review: the fact read the two predicates it was written for and none of "
            + $"the third. Outer WHERE was: {clauses[0]}");
    }

    private const string TableName = "alvo_outbox";
    private const string OuterWhereSeparator = "AND id IN (";

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(5);
}
