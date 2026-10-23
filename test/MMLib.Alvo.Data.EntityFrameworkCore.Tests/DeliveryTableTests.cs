using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

using System.Text.RegularExpressions;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The delivery statements' shape where behaviour cannot reach it: the claim repeats its claimability test outside the
/// subquery, which is what makes a second PostgreSQL claimant blocked behind the first claim nothing (EvalPlanQual).
/// </summary>
public sealed class DeliveryTableTests
{
    private const string Table = "alvo_deliveries";
    private const string Outbox = "alvo_outbox";

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void The_tables_are_named_from_the_framework_table_list()
    {
        DeliveryTable.NameFor("alvo").ShouldBe("alvo_deliveries");
        DeliveryTable.AttemptsNameFor("alvo").ShouldBe("alvo_delivery_attempts");
        SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain(DeliveryTable.NameFor("alvo"));
    }

    [Fact]
    public void One_event_is_delivered_once_per_subscriber() =>
        DeliveryTable.Ddl(Table).ShouldContain("UNIQUE (event_id, subscriber)");

    [Fact]
    public void The_claim_repeats_its_claimability_outside_the_subquery()
    {
        var sql = DeliveryTable.ClaimSql(Table, Outbox, ordered: 2, unordered: 1);

        Regex.Matches(sql, Regex.Escape("status IN ('pending', 'retrying')"), RegexOptions.None, _matchTimeout).Count.ShouldBe(2);
        Regex.Matches(sql, "NOT EXISTS", RegexOptions.None, _matchTimeout).Count.ShouldBe(4, "two lane checks in each half");
        sql.ShouldContain("subscriber IN (@s0, @s1, @s2)");
        sql.ShouldContain("subscriber IN (@s2) OR (NOT EXISTS");
        sql.ShouldContain("LIMIT @batch");
    }

    [Fact]
    public void An_ordered_lane_waits_for_earlier_entries_of_its_key_the_outbox_has_not_dispatched() =>
        DeliveryTable.ClaimSql(Table, Outbox, ordered: 1, unordered: 0)
            .ShouldContain($"FROM {Outbox} unfanned WHERE unfanned.partition_key = {Table}.partition_key");

    [Fact]
    public void An_earlier_entry_under_a_live_claim_blocks_even_at_the_ceiling() =>
        DeliveryTable.ClaimSql(Table, Outbox, ordered: 1, unordered: 0)
            .ShouldContain("unfanned.claimed_at >= @outbox_stale_before");

    /// <summary>
    /// Only an <c>unbound</c> dead delivery stops holding its lane. A dead row with no reason recorded must still hold
    /// it, and a bare <c>&lt;&gt; 'unbound'</c> is NULL — not true — for that row. No writer records one today, so the
    /// world cannot express it and the statement's shape is pinned instead.
    /// </summary>
    [Fact]
    public void A_dead_delivery_with_no_recorded_reason_still_holds_its_lane() =>
        DeliveryTable.ClaimSql(Table, Outbox, ordered: 1, unordered: 0)
            .ShouldContain("COALESCE(earlier.last_error_type, '') <> 'unbound'");

    [Fact]
    public void The_unfanned_index_is_partial_on_undispatched_entries() =>
        DeliveryTable.OutboxUnfannedIndexDdl(Outbox).ShouldEndWith("(partition_key, id) WHERE dispatched_at IS NULL");

    [Fact]
    public void A_claim_with_no_unordered_subscriber_orders_every_lane() =>
        DeliveryTable.ClaimSql(Table, Outbox, ordered: 1, unordered: 0).ShouldNotContain("OR (NOT EXISTS");
}
