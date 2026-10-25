using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

#pragma warning disable EF1001

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The operator's two tables (spec 2026-10-14 §4.8 <c>alvo_subscriber_controls</c>, E11 <c>alvo_operations_log</c>): named
/// from the framework table list, created with the spec's columns, and the log's idempotency key unique only when present.
/// </summary>
public sealed class DeliveryOperationsTableTests
{
    private const string Controls = "alvo_subscriber_controls";
    private const string Log = "alvo_operations_log";

    [Fact]
    public void The_tables_are_named_from_the_framework_table_list()
    {
        DeliveryOperationsTable.ControlsNameFor("alvo").ShouldBe(Controls);
        DeliveryOperationsTable.LogNameFor("alvo").ShouldBe(Log);
        SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain(Controls);
        SystemSchemaInitializer.FrameworkTableNames("alvo").ShouldContain(Log);
    }

    [Fact]
    public void The_control_table_carries_the_spec_s_columns_keyed_by_subscriber()
    {
        var ddl = DeliveryOperationsTable.ControlsDdl(Controls);

        ddl.ShouldContain("subscriber TEXT NOT NULL PRIMARY KEY");
        foreach (var column in new[] { "paused_at", "paused_by", "reason", "disabled_at", "disabled_by" })
        {
            ddl.ShouldContain($"{column} TEXT NULL");
        }
    }

    [Fact]
    public void The_log_carries_e11_s_columns_and_the_replay_s_record()
    {
        var ddl = DeliveryOperationsTable.LogDdl(Log);

        foreach (var column in new[] { "id TEXT NOT NULL PRIMARY KEY", "at TEXT NOT NULL", "actor TEXT NOT NULL", "operation TEXT NOT NULL", "target TEXT NOT NULL", "reason TEXT NULL" })
        {
            ddl.ShouldContain(column);
        }

        foreach (var column in new[] { "affected INTEGER NULL", "sample TEXT NULL", "idempotency_scope TEXT NULL", "idempotency_key TEXT NULL", "request_fingerprint TEXT NULL" })
        {
            ddl.ShouldContain(column);
        }
    }

    [Fact]
    public void A_key_is_unique_per_caller_only_when_one_was_sent() =>
        DeliveryOperationsTable.LogKeyIndexDdl(Log).ShouldBe(
            $"CREATE UNIQUE INDEX IF NOT EXISTS ux_{Log}_key ON {Log} (idempotency_scope, idempotency_key) WHERE idempotency_key IS NOT NULL");

    /// <summary>
    /// A key is never stored without its scope: a <c>NULL</c> scope would make the unique index treat every such key as
    /// distinct, so a retried replay would run twice. The table refuses the row instead of trusting every writer.
    /// </summary>
    [Fact]
    public void A_key_is_never_stored_without_its_scope() =>
        DeliveryOperationsTable.LogDdl(Log).ShouldContain("CHECK (idempotency_key IS NULL OR idempotency_scope IS NOT NULL)");

    [Fact]
    public void A_pause_changes_only_a_running_subscriber() =>
        DeliveryOperationsTable.PauseSql(Controls).ShouldContain($"WHERE {Controls}.paused_at IS NULL");

    [Fact]
    public void A_resume_changes_only_a_paused_subscriber() =>
        DeliveryOperationsTable.ResumeSql(Controls).ShouldEndWith("WHERE subscriber = @subscriber AND paused_at IS NOT NULL");

    [Fact]
    public void Bringing_the_tables_up_is_idempotent() =>
        DeliveryOperationsTable.Statements("alvo_deliveries", Controls, Log).ShouldAllBe(statement => statement.Contains("IF NOT EXISTS"));

    /// <summary>A page of one subscriber has its own index, brought up with the operator's tables.</summary>
    [Fact]
    public void A_page_of_one_subscriber_has_its_own_index()
    {
        var ddl = DeliveryOperationsTable.SubscriberPageIndexDdl("alvo_deliveries");

        ddl.ShouldBe("CREATE INDEX IF NOT EXISTS ix_alvo_deliveries_subscriber_id ON alvo_deliveries (subscriber, id)");
        DeliveryOperationsTable.Statements("alvo_deliveries", Controls, Log).ShouldContain(ddl);
    }

    /// <summary>An unfiltered page has no <c>WHERE</c>; it seeks by id, newest first, and its size is a parameter.</summary>
    [Fact]
    public void An_unfiltered_page_seeks_by_id_newest_first() =>
        DeliveryOperationsTable.ListSql("alvo_deliveries", new() { Limit = 5 }).ShouldBe(
            $"SELECT {DeliveryOperationsTable.DeliveryColumns} FROM alvo_deliveries ORDER BY id DESC LIMIT @limit");

    /// <summary>Every filter value is a parameter, and the keyset term belongs to a page only, never to a selection.</summary>
    [Fact]
    public void Every_filter_value_is_a_parameter_and_the_keyset_term_is_a_page_s_only()
    {
        var filter = new MMLib.Alvo.Events.DeliveryFilter
        {
            Subscriber = "x'; DROP TABLE t; --",
            PartitionKey = "tickets:a",
            Status = "dead",
            FromEventId = Guid.NewGuid(),
            ToEventId = Guid.NewGuid(),
            After = Guid.NewGuid(),
            Limit = 5,
        };
        const string Terms =
            "subscriber = @subscriber AND partition_key = @partition_key AND status = @status AND event_id >= @from_event AND event_id < @to_event";

        DeliveryOperationsTable.Conditions(filter, keyset: true).ShouldBe(Terms + " AND id < @after");
        DeliveryOperationsTable.Conditions(filter, keyset: false).ShouldBe(Terms);
    }

    [Fact]
    public void A_redeliver_resets_only_a_finished_row_whose_lane_runs_nothing()
    {
        var sql = DeliveryOperationsTable.RedeliverSql("alvo_deliveries");

        sql.ShouldContain("status IN ('succeeded', 'dead', 'cancelled')");
        sql.ShouldContain("attempts = 0");
        sql.ShouldContain("NOT EXISTS (SELECT 1 FROM alvo_deliveries lane");
    }

    [Fact]
    public void A_log_row_under_a_spent_key_is_skipped_not_failed() =>
        DeliveryOperationsTable.InsertLogSql(Log).ShouldEndWith("ON CONFLICT DO NOTHING");

    /// <summary>
    /// A subscriber's lock key is FNV-1a over the UTF-8 of the table and the subscriber — computed in .NET, not by the
    /// server, so every process derives the same key from the same name, and pinned to the published test vector.
    /// </summary>
    [Fact]
    public void A_subscriber_s_lock_key_is_a_stable_64_bit_hash_of_its_table_and_name()
    {
        DeliveryOperationsTable.LockKey("a").ShouldBe(unchecked((long)0xaf63dc4c8601ec8cUL));
        DeliveryOperationsTable.LockKey(string.Empty).ShouldBe(unchecked((long)0xcbf29ce484222325UL));
        DeliveryOperationsTable.SubscriberLockKey("alvo_deliveries", "send-confirmation")
            .ShouldBe(DeliveryOperationsTable.LockKey("alvo_deliveries:send-confirmation"));
        DeliveryOperationsTable.SubscriberLockKey("other_deliveries", "send-confirmation")
            .ShouldNotBe(DeliveryOperationsTable.SubscriberLockKey("alvo_deliveries", "send-confirmation"));
    }

    [Fact]
    public void A_replay_resets_only_replayable_rows_whose_lane_runs_nothing_and_reads_back_what_it_reset()
    {
        var sql = DeliveryOperationsTable.ReplaySql("alvo_deliveries", new MMLib.Alvo.Events.DeliveryFilter { Subscriber = "a", Status = "dead", Limit = 20 });

        sql.ShouldContain("WHERE status IN ('succeeded', 'dead', 'cancelled') AND COALESCE(last_error_type, '') <> 'chain-depth-exceeded' AND subscriber = @subscriber AND status = @status");
        sql.ShouldContain("AND NOT EXISTS (SELECT 1 FROM alvo_deliveries lane");
        sql.ShouldNotContain("@replay_subscriber");
        sql.ShouldEndWith("RETURNING id, event_id");
    }

    /// <summary>
    /// A replay naming no subscriber resets only the subscribers whose locks it took: a subscriber whose first replayable
    /// row appeared after the transaction read the set is left for the next replay, never reset unlocked.
    /// </summary>
    [Fact]
    public void A_replay_naming_no_subscriber_resets_only_the_subscribers_it_locked()
    {
        var sql = DeliveryOperationsTable.ReplaySql("alvo_deliveries", new MMLib.Alvo.Events.DeliveryFilter { Status = "dead", Limit = 20 }, lockedSubscribers: 2);

        sql.ShouldContain("AND subscriber IN (@replay_subscriber0, @replay_subscriber1)");
        Should.Throw<ArgumentOutOfRangeException>(() => DeliveryOperationsTable.ReplaySql("alvo_deliveries", new MMLib.Alvo.Events.DeliveryFilter { Limit = 20 }));
        DeliveryOperationsTable.ReplaySubscribersSql("alvo_deliveries", new MMLib.Alvo.Events.DeliveryFilter { Status = "dead", Limit = 20 })
            .ShouldBe("SELECT DISTINCT subscriber FROM alvo_deliveries WHERE status IN ('succeeded', 'dead', 'cancelled') AND COALESCE(last_error_type, '') <> 'chain-depth-exceeded' AND status = @status");
    }

    [Fact]
    public void A_replay_s_outcome_is_written_onto_its_row_and_a_key_is_found_within_its_scope()
    {
        DeliveryOperationsTable.LogOutcomeSql(Log).ShouldBe($"UPDATE {Log} SET affected = @log_affected, sample = @log_sample WHERE id = @log_id");
        DeliveryOperationsTable.FindLogSql(Log).ShouldEndWith("WHERE idempotency_scope = @scope AND idempotency_key = @key");
    }

    /// <summary>
    /// A dialect that does not answer the transaction lock refuses rather than answering "no lock": an engine whose writers
    /// run concurrently must never inherit SQLite's answer by omission.
    /// </summary>
    [Fact]
    public void A_dialect_that_does_not_answer_the_transaction_lock_refuses() =>
        Should.Throw<NotSupportedException>(() => ((IAlvoSqlDialect)new TestSqlDialect()).TransactionLockStatement(["@lock0"], exclusive: true));
}
