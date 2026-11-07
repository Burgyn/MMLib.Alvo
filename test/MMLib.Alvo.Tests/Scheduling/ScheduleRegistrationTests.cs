using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Scheduling;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Scheduling;

/// <summary>
/// <c>AddSchedule</c> at boot: every cron, zone and scope problem refused at once through the boot's own catalog-free check,
/// the extension id grammar, and the catalog entry a schedule is listed as.
/// </summary>
public sealed class ScheduleRegistrationTests
{
    [Fact]
    public void An_each_tenant_schedule_without_a_tenant_catalog_refuses_boot()
    {
        var refusal = Should.Throw<AlvoStartupRefusedException>(() => EnsureRegistrable(alvo => alvo
            .AddSchedule<NightlyRoll>("nightly-roll", "0 2 * * *", configure: options => options.Scope = AlvoScheduleScope.EachTenant)));

        refusal.Message.ShouldContain("'nightly-roll' (schedule) at extensions/nightly-roll/scope");
        refusal.FixSuggestion.ShouldContain("IAlvoTenantCatalog");
    }

    [Fact]
    public void An_each_tenant_schedule_with_a_tenant_catalog_starts() =>
        Should.NotThrow(() => EnsureRegistrable(alvo => alvo
            .AddSchedule<NightlyRoll>("nightly-roll", "0 2 * * *", configure: options => options.Scope = AlvoScheduleScope.EachTenant)
            .Services.AddSingleton<IAlvoTenantCatalog, NoTenants>()));

    [Fact]
    public void Every_cron_and_zone_problem_is_refused_at_boot_at_once()
    {
        var refusal = Should.Throw<AlvoStartupRefusedException>(() => EnsureRegistrable(alvo => alvo
            .AddSchedule<NightlyRoll>("nightly-roll", "0 0 2 * * *")
            .AddSchedule<NightlyRoll>("weekly-report", "0 6 * * 1", "Europe/Atlantis")));

        refusal.Message.ShouldContain("2 problem(s)");
        refusal.Message.ShouldContain("extensions/nightly-roll/cron");
        refusal.Message.ShouldContain("extensions/weekly-report/timeZone");
    }

    [Fact]
    public void A_schedule_id_follows_the_extension_grammar_and_collides_with_a_handler_id()
    {
        var refusal = Should.Throw<AlvoStartupRefusedException>(() => EnsureRegistrable(alvo => alvo
            .AddSchedule<NightlyRoll>("Nightly_Roll", "0 2 * * *")
            .AddSchedule<NightlyRoll>("orders-approved", "0 2 * * *")
            .AddEventHandler("orders-approved", "orders.approved", OnRecord)));

        refusal.Message.ShouldContain("extensions/Nightly_Roll/id");
        refusal.Message.ShouldContain("'orders-approved' is registered 2 times (schedule, eventHandler)");
    }

    [Fact]
    public void The_catalog_lists_a_schedule_with_its_kind_stage_cron_and_zone()
    {
        var registry = Registry(alvo => alvo
            .AddEventHandler("orders-approved", "orders.approved", OnRecord)
            .AddSchedule<NightlyRoll>("nightly-roll", "0 2 * * *", "Europe/Bratislava", options => options.Description = "Rolls the day over."));

        var catalog = new ExtensionCatalog(registry, Options.Create(new AlvoExtensionOptions()));
        var schedule = catalog.Items.Single(item => item.Id == "nightly-roll");

        (schedule.Kind, schedule.Stage, schedule.Event).ShouldBe((AlvoExtensionKind.Schedule, AlvoExtensionStage.Scheduled, "alvo.schedule.nightly_roll"));
        (schedule.Cron, schedule.TimeZone, schedule.Timeout, schedule.RunAs).ShouldBe(("0 2 * * *", "Europe/Bratislava", TimeSpan.FromMinutes(15), "system"));
        schedule.Description.ShouldBe("Rolls the day over.");
        catalog.Items.Single(item => item.Id == "orders-approved").Cron.ShouldBeNull();
        catalog.HonouredKinds.ShouldBe(["extensions.eventHandler", "extensions.schedule"]);
        ExtensionCatalog.WireName(AlvoExtensionKind.Schedule).ShouldBe("schedule");
        ((int)AlvoExtensionKind.Schedule, (int)AlvoExtensionStage.Scheduled).ShouldBe((9, 5), "7 and 4 are the read extensions', 8 is reserved");
        System.Text.Json.JsonSerializer.Serialize(AlvoExtensionStage.Scheduled).ShouldBe("\"scheduled\"");
    }

    [Fact]
    public void A_schedule_fans_out_as_a_schedule_delivery_with_no_ordering()
    {
        var registration = Registry(alvo => alvo.AddSchedule<NightlyRoll>("nightly-roll", "0 2 * * *")).Handlers.ShouldHaveSingleItem();

        registration.Job.ShouldNotBeNull().DeliveryKind.ShouldBe("schedule");
        (registration.Settings.Ordering, registration.Settings.RunAs).ShouldBe((AlvoOrdering.None, AlvoRunAs.System));
    }

    [Fact]
    public void The_catalog_lists_a_migration_with_its_revision()
    {
        var registry = Registry(alvo => alvo.AddDataMigration<BackfillNotes>("backfill-notes", afterRevision: 2));

        var catalog = new ExtensionCatalog(registry, Options.Create(new AlvoExtensionOptions()));
        var migration = catalog.Items.ShouldHaveSingleItem();

        (migration.Kind, migration.Stage, migration.Event).ShouldBe((AlvoExtensionKind.DataMigration, AlvoExtensionStage.Scheduled, "alvo.migration.backfill_notes"));
        (migration.AfterRevision, migration.Cron, migration.Timeout, migration.RunAs).ShouldBe(((int?)2, (string?)null, TimeSpan.FromMinutes(15), "system"));
        ExtensionCatalog.WireName(AlvoExtensionKind.DataMigration).ShouldBe("dataMigration");
        ((int)AlvoExtensionKind.DataMigration).ShouldBe(10, "7 and 8 are the read extensions', 9 the schedule's");
    }

    [Fact]
    public void A_migration_fans_out_as_a_migration_delivery_with_no_ordering()
    {
        var registration = Registry(alvo => alvo.AddDataMigration<BackfillNotes>("backfill-notes", afterRevision: 1)).Handlers.ShouldHaveSingleItem();

        registration.Job.ShouldNotBeNull().DeliveryKind.ShouldBe("migration");
        registration.Job.AfterRevision.ShouldBe(1);
        (registration.Settings.Ordering, registration.Settings.RunAs).ShouldBe((AlvoOrdering.None, AlvoRunAs.System));
    }

    [Fact]
    public void A_host_migration_never_spans_tenants() =>
        Registry(alvo => alvo.AddDataMigration<BackfillNotes>("backfill-notes", afterRevision: 1)).Handlers.ShouldHaveSingleItem()
            .Job.ShouldNotBeNull().SpansTenants.ShouldBeFalse("only the framework's timer re-plan reads every tenant below the policy layer");

    [Theory]
    [InlineData("/entities/bookings/timers#27dfb2eb")]
    [InlineData("/automation/notify/actions#1f0c9a2e")]
    public void A_host_registration_cannot_take_a_descriptor_owned_id(string id)
    {
        var refusal = Should.Throw<AlvoStartupRefusedException>(() => EnsureRegistrable(alvo => alvo
            .AddDataMigration<BackfillNotes>(id, afterRevision: 1)
            .AddEventHandler(id, "orders.approved", OnRecord)));

        refusal.Message.ShouldContain($"'{id}' is not an extension id.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_migration_waits_for_revision_one_or_later(int afterRevision) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Registry(alvo => alvo.AddDataMigration<BackfillNotes>("backfill-notes", afterRevision)));

    private static void EnsureRegistrable(Action<IAlvoBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddAlvo(configure);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ExtensionBinder>().EnsureRegistrable();
    }

    private sealed class NightlyRoll : IAlvoScheduledJob
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BackfillNotes : IAlvoDataMigration
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoTenants : IAlvoTenantCatalog
    {
        public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>([]);
    }
}
