using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The outbox table's shape, with no database in sight: what its DDL may and may not contain, and that the
/// introspector is told about it.
/// </summary>
/// <remarks>
/// Every claim here is about a decision rather than about behaviour, which is why it is asserted over the DDL
/// text: an ordering key that is a per-engine identity column, or a monotonic integer for someone to build a
/// high-water mark on, would both work on the engine and both be wrong — the first breaks
/// <see cref="SystemSchemaInitializer"/>'s no-branching invariant, the second drops rows silently because
/// PostgreSQL sequences commit out of order.
/// </remarks>
public class OutboxTableTests
{
    /// <summary>
    /// One <c>CREATE TABLE IF NOT EXISTS</c> over ANSI-portable types, and none of the identity spellings that
    /// would force a per-engine branch.
    /// </summary>
    [Fact]
    public void The_ddl_is_identical_ansi_portable_with_no_per_engine_branching()
    {
        var ddl = OutboxTable.Ddl(_outboxTableName);

        ddl.ShouldContain($"CREATE TABLE IF NOT EXISTS {_outboxTableName}");
        foreach (var perEngine in _perEngineIdentitySpellings)
        {
            ddl.ShouldNotContain(
                perEngine,
                Case.Insensitive,
                "SystemSchemaInitializer's stated invariant is identical ANSI-portable DDL on both engines "
                + "with no per-engine branching; the ordering key is a UUIDv7 id instead (plan decision D1, "
                + "spike Q1/Q6). SERIAL is in this list even though SQLite ACCEPTS it: SQLite parses it as an "
                + "unrecognised column type and gives a nullable column that never increments, so it would "
                + "pass CI and lose ordering in production");
        }
    }

    private static readonly string[] _perEngineIdentitySpellings =
        ["AUTOINCREMENT", "IDENTITY", "SERIAL", "nextval", "bigserial"];

    /// <summary>
    /// R2: a high-water mark over a monotonic integer silently drops a row, because PostgreSQL sequences commit
    /// out of order — one transaction can take 100 and commit after another took 101 and committed. There is no
    /// such column to be tempted by, and that is asserted rather than intended.
    /// </summary>
    [Fact]
    public void There_is_no_sequence_column()
        => OutboxTable.Ddl(_outboxTableName).ShouldNotContain("sequence", Case.Insensitive);

    /// <summary>
    /// The column F7's partitioned claim will read is written from this first migration, so that change is
    /// additive rather than a migration of a shipped table.
    /// </summary>
    [Fact]
    public void Partition_key_is_written_from_the_first_migration_even_though_nothing_reads_it_in_f3()
        => OutboxTable.Ddl(_outboxTableName).ShouldContain("partition_key TEXT NOT NULL");

    /// <summary>
    /// The claim's two state columns must be nullable, because the queue's state machine is their nullness —
    /// <c>claimed_at IS NULL</c> and <c>dispatched_at IS NULL</c> — and a <c>NOT NULL</c> column would force a
    /// sentinel value that no portable claim statement could test for.
    /// </summary>
    /// <param name="column">The column whose nullability the claim depends on.</param>
    [Theory]
    [InlineData("claimed_at")]
    [InlineData("dispatched_at")]
    public void The_claims_state_columns_are_nullable(string column)
        => OutboxTable.Ddl(_outboxTableName).ShouldContain($"{column} TEXT NULL");

    /// <summary>
    /// The name the introspector must exclude. Without this, the next re-apply plans a <c>DROP</c> for the
    /// table — silently, and the symptom is a lost event history rather than an error.
    /// </summary>
    [Fact]
    public void The_outbox_is_one_of_the_framework_tables_the_introspector_excludes()
        => SystemSchemaInitializer.FrameworkTableNames(SchemaPrefix).ShouldContain(_outboxTableName);

    /// <summary>
    /// The table's name is derived from the prefix rather than fixed, so a host that renamed Alvo's schema
    /// prefix gets its outbox renamed with everything else.
    /// </summary>
    [Fact]
    public void The_table_name_follows_the_hosts_own_schema_prefix()
        => OutboxTable.NameFor("tenant_a").ShouldBe("tenant_a_outbox");

    private const string SchemaPrefix = "alvo";

    private static readonly string _outboxTableName = OutboxTable.NameFor(SchemaPrefix);
}
