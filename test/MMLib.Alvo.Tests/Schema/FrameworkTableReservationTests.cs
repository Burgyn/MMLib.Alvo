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
    public void The_reserved_set_is_the_seventeen_bookkeeping_tables_the_five_account_tables_and_the_seven_identity_ones()
    {
        AlvoFrameworkTables.NamesFor("alvo").Count.ShouldBe(29);
        AlvoFrameworkTables.IdentitySuffixes.Count.ShouldBe(7);
    }

    [Fact]
    public void The_sequence_counter_table_is_reserved() =>
        AlvoFrameworkTables.NamesFor("alvo").ShouldContain("alvo_sequences");

    [Fact]
    public void The_delivery_tables_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_deliveries");
        names.ShouldContain("alvo_delivery_attempts");
        names.ShouldContain("alvo_subscriber_controls");
        names.ShouldContain("alvo_operations_log");
    }

    [Fact]
    public void The_command_and_inbound_webhook_ledgers_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_command_requests");
        names.ShouldContain("alvo_inbound_webhooks");
    }

    [Fact]
    public void The_timer_schedule_and_data_migration_tables_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_timers");
        names.ShouldContain("alvo_schedules");
        names.ShouldContain("alvo_data_migrations");
    }

    [Fact]
    public void The_step_and_concurrency_slot_tables_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_steps");
        names.ShouldContain("alvo_concurrency_slots");
    }

    [Fact]
    public void The_end_user_account_tables_are_reserved()
    {
        var names = AlvoFrameworkTables.NamesFor("alvo");

        names.ShouldContain("alvo_auth_accounts");
        names.ShouldContain("alvo_auth_identities");
        names.ShouldContain("alvo_auth_sessions");
        names.ShouldContain("alvo_auth_refresh_tokens");
        names.ShouldContain("alvo_auth_keys");
    }

    [Fact]
    public void The_reservation_follows_a_custom_schema_prefix()
        => AlvoFrameworkTables.NamesFor("acme").ShouldAllBe(name => name.StartsWith("acme"));
}
