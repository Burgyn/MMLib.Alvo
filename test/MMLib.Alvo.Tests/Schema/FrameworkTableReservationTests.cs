namespace MMLib.Alvo.Tests.Schema;

/// <summary>
/// The identity subsystem's tables are the framework's, so introspection excludes them and a
/// descriptor may not claim one.
/// </summary>
/// <remarks>
/// A table <c>MMLib.Alvo.Identity</c> creates and <see cref="AlvoFrameworkTables"/> does not name
/// is a table the next re-apply plans to <c>DROP</c> — every operator account, silently, on a
/// descriptor edit. That is the #156 failure one subsystem over, and it is why the names are
/// reserved here before the package that creates them exists.
/// </remarks>
public class FrameworkTableReservationTests
{
    [Fact]
    public void Every_identity_table_is_reserved_under_the_configured_prefix()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        foreach (var suffix in AlvoFrameworkTables.IdentitySuffixes)
        {
            names.ShouldContain("alvo" + suffix);
        }
    }

    [Fact]
    public void The_reserved_set_is_the_three_bookkeeping_tables_plus_the_seven_identity_ones()
    {
        AlvoFrameworkTables.NamesFor("alvo").Count.ShouldBe(10);
        AlvoFrameworkTables.IdentitySuffixes.Count.ShouldBe(7);
    }

    [Fact]
    public void The_reservation_follows_a_custom_schema_prefix()
        => AlvoFrameworkTables.NamesFor("acme").ShouldAllBe(name => name.StartsWith("acme"));
}
