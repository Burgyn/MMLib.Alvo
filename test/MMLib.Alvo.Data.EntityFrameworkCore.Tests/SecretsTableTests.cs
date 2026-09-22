using MMLib.Alvo.Data.EntityFrameworkCore.Internal;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The secrets table's DDL, held to the one invariant every framework table in this package shares.
/// </summary>
/// <remarks>
/// The same assertions <c>OutboxTableTests</c> makes, for the same measured reason: each shipped engine
/// refuses the other's auto-increment spelling, and SQLite <em>accepts</em> <c>SERIAL</c> as an unrecognised
/// type that silently never increments — so a portable-looking column passes every SQLite test in CI and is
/// wrong in production.
/// </remarks>
public sealed class SecretsTableTests
{
    [Fact]
    public void The_name_is_the_prefix_and_the_reserved_suffix() =>
        SecretsTable.NameFor("alvo").ShouldBe("alvo_secrets");

    /// <summary>The name is reserved, so an entity cannot be called it and introspection excludes it.</summary>
    [Fact]
    public void The_table_is_one_the_framework_owns() =>
        AlvoFrameworkTables.NamesFor("alvo").ShouldContain("alvo_secrets");

    [Fact]
    public void The_ddl_carries_no_engine_specific_auto_increment()
    {
        var ddl = SecretsTable.Ddl("alvo_secrets");

        ddl.ShouldNotContain("SERIAL", Case.Insensitive);
        ddl.ShouldNotContain("AUTOINCREMENT", Case.Insensitive);
        ddl.ShouldNotContain("IDENTITY", Case.Insensitive);
    }

    /// <summary>The name is the key, so one name can only ever hold one value.</summary>
    [Fact]
    public void The_name_is_the_primary_key() =>
        SecretsTable.Ddl("alvo_secrets").ShouldContain("name TEXT NOT NULL PRIMARY KEY");

    /// <summary>A repeat run of the apply path must not fail on a table that is already there.</summary>
    [Fact]
    public void The_ddl_is_safe_to_run_repeatedly() =>
        SecretsTable.Ddl("alvo_secrets").ShouldStartWith("CREATE TABLE IF NOT EXISTS");

    /// <summary>
    /// The listing reads names and never values.
    /// </summary>
    /// <remarks>
    /// Asserted on the statement rather than on a result, because the failure this guards is a widened
    /// <c>SELECT</c> that no behavioural test would notice: every caller would keep working, and every
    /// secret this deployment holds would be in one result set.
    /// </remarks>
    [Fact]
    public void The_listing_selects_no_value() =>
        SecretsTable.ListNamesSql("alvo_secrets").ShouldNotContain("value");

    /// <summary>A write is one statement, which is what keeps the store to one per call.</summary>
    [Fact]
    public void A_write_is_one_upsert_rather_than_a_read_and_a_write()
    {
        var sql = SecretsTable.UpsertSql("alvo_secrets");

        sql.ShouldStartWith("INSERT INTO");
        sql.ShouldContain("ON CONFLICT (name) DO UPDATE");
    }
}
