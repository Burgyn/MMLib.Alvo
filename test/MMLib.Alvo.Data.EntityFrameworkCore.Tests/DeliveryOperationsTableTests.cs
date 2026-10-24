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
    public void Bringing_the_tables_up_is_idempotent() =>
        DeliveryOperationsTable.Statements(Controls, Log).ShouldAllBe(statement => statement.Contains("IF NOT EXISTS"));
}
