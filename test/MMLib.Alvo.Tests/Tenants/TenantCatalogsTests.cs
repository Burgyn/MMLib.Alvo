using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;
using MMLib.Alvo.Tests.Expressions;

using NSubstitute;

using static MMLib.Alvo.Tests.Extensions.HandlerCatalog;

namespace MMLib.Alvo.Tests.Tenants;

/// <summary>
/// The default tenant list (spec §6.6, S3 plan D13): the self-service registry lists the tenants only while the descriptor
/// judged declares <c>tenancy.selfService</c> and the host registers no <see cref="IAlvoTenantCatalog"/> — so the boot's
/// refusal of a per-tenant run without a tenant list keeps firing everywhere else, and a host's own catalog always wins.
/// </summary>
public sealed class TenantCatalogsTests
{
    private const string Nightly = "nightly-roll";
    private const string Backfill = "backfill-notes";

    private static readonly TenantId _personal = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly TenantId _shared = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly TenantId _hostOnly = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    private static readonly DateTimeOffset _now = new(2026, 11, 14, 2, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void An_each_tenant_schedule_without_a_catalog_still_refuses_boot()
    {
        using var host = Host(registry: true);
        var binder = host.GetRequiredService<ExtensionBinder>();

        Should.NotThrow(binder.EnsureRegistrable, "a registry is a tenant list under a descriptor that declares selfService");
        var refusal = Should.Throw<AlvoStartupRefusedException>(() => binder.EnsureBindable(Catalog(selfService: false)));

        refusal.Message.ShouldContain($"'{Nightly}' (schedule) at extensions/{Nightly}/scope");
        refusal.Message.ShouldContain(
            $"'{Nightly}' runs once per tenant, and nothing lists them under this descriptor: this host registers no "
            + "IAlvoTenantCatalog, and the descriptor does not declare tenancy.selfService, whose registry would.");
        refusal.FixSuggestion.ShouldContain(
            "Declare tenancy.selfService in the descriptor, or register an IAlvoTenantCatalog in DI, or use AlvoScheduleScope.Global.",
            customMessage: "on a host with a registry the descriptor is the cure the refusal names first");
        using var bare = Host(registry: false);
        var bareRefusal = Should.Throw<AlvoStartupRefusedException>(bare.GetRequiredService<ExtensionBinder>().EnsureRegistrable);
        bareRefusal.Message.ShouldContain($"'{Nightly}' runs once per tenant, and this host registers no IAlvoTenantCatalog to list them.");
        bareRefusal.FixSuggestion.ShouldContain("Register an IAlvoTenantCatalog in DI, or use AlvoScheduleScope.Global.");
        bareRefusal.FixSuggestion.ShouldNotContain("selfService", customMessage: "no descriptor can cure a host with neither");
    }

    [Fact]
    public async Task Self_service_lists_the_registry_s_tenants()
    {
        using var host = Host(registry: true);
        var catalog = Apply(host, Catalog(selfService: true, personalTenant: true));
        var binder = host.GetRequiredService<ExtensionBinder>();

        binder.EnsureRegistrable();
        binder.EnsureBindable(catalog);
        var runs = await host.GetRequiredService<ScheduleRuns>().EventsAsync(Schedule(host), _now, manual: false, Ct);

        host.GetRequiredService<TenantCatalogs>().Current.ShouldBeOfType<RegistryTenantCatalog>();
        runs.Select(run => run.Envelope.TenantId).ShouldBe([_personal.ToString(), _shared.ToString()]);
    }

    /// <summary>
    /// The default list honours what the applied descriptor honours (review I2): without <c>personalTenant</c> nobody can act
    /// in a personal tenant, so no per-tenant run is minted for one — and putting it back lists them again.
    /// </summary>
    [Fact]
    public async Task Without_personal_tenant_the_registry_lists_only_shared_tenants()
    {
        using var host = Host(registry: true);
        var runs = host.GetRequiredService<ScheduleRuns>();

        Apply(host, Catalog(selfService: true, personalTenant: false));
        var shared = await runs.EventsAsync(Schedule(host), _now, manual: false, Ct);
        Apply(host, Catalog(selfService: true, personalTenant: true));
        var both = await runs.EventsAsync(Schedule(host), _now, manual: false, Ct);

        shared.ShouldHaveSingleItem().Envelope.TenantId.ShouldBe(_shared.ToString());
        both.Select(run => run.Envelope.TenantId).ShouldBe([_personal.ToString(), _shared.ToString()]);
    }

    [Fact]
    public async Task A_host_catalog_wins_over_the_registry()
    {
        var own = new FixedTenants(_hostOnly);
        using var host = Host(registry: true, own);
        Apply(host, Catalog(selfService: true));
        var catalogs = host.GetRequiredService<TenantCatalogs>();

        var runs = await host.GetRequiredService<ScheduleRuns>().EventsAsync(Schedule(host), _now, manual: false, Ct);

        (catalogs.Current, catalogs.For(SelfServicePolicy.None)).ShouldBe((own, own));
        runs.ShouldHaveSingleItem().Envelope.TenantId.ShouldBe(_hostOnly.ToString());
    }

    [Fact]
    public async Task Dropping_self_service_stops_listing_the_registry()
    {
        using var host = Host(registry: true);
        var catalogs = host.GetRequiredService<TenantCatalogs>();
        Apply(host, Catalog(selfService: true));
        catalogs.Current.ShouldNotBeNull();

        Apply(host, Catalog(selfService: false));

        catalogs.Current.ShouldBeNull();
        host.GetRequiredService<ScheduleRuns>().ListsTenants(SelfServicePolicy.None).ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() =>
            host.GetRequiredService<ScheduleRuns>().EventsAsync(Schedule(host), _now, manual: false, Ct));
    }

    /// <summary>
    /// The other half of moving the refusal out of the catalog-free pass: once a <c>selfService</c> descriptor has bound
    /// an each-tenant schedule to the registry, an apply that drops <c>selfService</c> would unbind it — and the apply guard
    /// refuses that as it refuses any unbinding, naming the schedule; re-applying with <c>selfService</c> passes clean.
    /// </summary>
    [Fact]
    public void An_apply_dropping_self_service_under_a_bound_each_tenant_schedule_is_refused()
    {
        using var host = Host(registry: true);
        Apply(host, Catalog(selfService: true));
        var guard = host.GetRequiredService<ExtensionApplyGuard>();

        var refusal = Should.Throw<DescriptorValidationException>(() => guard.Check(Catalog(selfService: false)));

        refusal.Result.Errors.ShouldContain(error =>
            error.Severity == DescriptorValidationSeverity.Error && error.Message.Contains($"'{Nightly}'", StringComparison.Ordinal));
        guard.Check(Catalog(selfService: true)).Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_tenanted_automation_compiles_with_the_registry_only_under_self_service(bool registry, bool selfService, bool compiles)
    {
        var descriptor = AlvoDescriptor.Parse(Descriptor(selfService, CronAutomation));
        var host = new DescriptorHost(new Dictionary<string, FunctionRegistration>(StringComparer.Ordinal), HasTenantCatalog: false, HasActionLedger: false)
        {
            HasTenantRegistry = registry,
        };

        PolicyCatalog Build() => PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler, host);

        if (compiles)
        {
            Build().Automation.ShouldHaveSingleItem().Trigger.ShouldBeOfType<CronTrigger>().PerTenant.ShouldBeTrue();
        }
        else
        {
            var refused = Should.Throw<DescriptorValidationException>(Build).Result.Errors
                .Single(error => error.Path == "/automation/close-day/trigger/schedule");
            refused.FixSuggestion.ShouldBe(registry
                ? "Declare tenancy.selfService in the descriptor, or register an IAlvoTenantCatalog in DI, or trigger the rule from an entity event."
                : "Register an IAlvoTenantCatalog, or trigger the rule from an entity event.");
        }
    }

    [Fact]
    public async Task A_migration_on_a_scoped_schema_boots_with_the_registry_under_self_service()
    {
        var registry = Registry(alvo => alvo.AddDataMigration<BackfillNotes>(Backfill, afterRevision: 1));
        var check = new DataMigrationBootCheck(registry, OnlyTheRegistry(), store: null);

        await check.RefuseAsync(Catalog(selfService: true), Ct);
        var refusal = await Should.ThrowAsync<AlvoStartupRefusedException>(() => check.RefuseAsync(Catalog(selfService: false), Ct));

        refusal.Message.ShouldContain($"extensions/{Backfill}/tenants");
        refusal.FixSuggestion.ShouldContain("Declare tenancy.selfService in the descriptor, or register an IAlvoTenantCatalog in DI.");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task A_migration_starts_once_per_registry_tenant_only_at_a_self_service_revision(bool selfService, bool personalTenant)
    {
        using var host = Host(registry: true);
        var store = Substitute.For<ITimerStore>();
        store.StartedMigrationsAsync(Arg.Any<CancellationToken>()).Returns([]);
        store.StartMigrationAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<AlvoFrameworkEnvelope>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var catalog = Catalog(selfService, personalTenant);
        var applied = Substitute.For<IAppliedSchemaStore>();
        applied.GetCurrentAsync("desk", Arg.Any<CancellationToken>())
            .Returns(new AppliedSchema(catalog.Schema, Descriptor(selfService, personalTenant: personalTenant), 2, _now));
        var starter = new DataMigrationStarter(host.GetRequiredService<ScheduleRuns>(), applied, TimeProvider.System, NullLogger<DataMigrationStarter>.Instance);
        var bindings = ExtensionBindingCompiler.Compile(
            Registry(alvo => alvo.AddDataMigration<BackfillNotes>(Backfill, afterRevision: 1)), catalog, CelFixtures.Compiler, EveryService()).Bindings;

        var round = await starter.StartDueAsync(store, bindings, "desk", Ct);

        round.Started.ShouldBe(selfService ? 1 : 0, "without selfService the registry is no tenant list, so the start is held");
        var tenants = store.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ITimerStore.StartMigrationAsync))
            .SelectMany(call => (IReadOnlyList<AlvoFrameworkEnvelope>)call.GetArguments()[2]!).Select(run => run.Envelope.TenantId);
        tenants.ShouldBe(
            !selfService ? [] : personalTenant ? [_personal.ToString(), _shared.ToString()] : [_shared.ToString()],
            "the revision's own policy decides which registry tenants count, personal ones only with personalTenant");
    }

    private static ServiceProvider Host(bool registry, IAlvoTenantCatalog? own = null)
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.AddSchedule<NightlyRoll>(Nightly, "0 2 * * *", configure: options => options.Scope = AlvoScheduleScope.EachTenant));
        if (registry)
        {
            services.AddSingleton<ITenantStore>(
                new ListingRegistry(new StoredTenant(_personal, TenantKind.Personal), new StoredTenant(_shared, TenantKind.Shared)));
        }

        if (own is not null)
        {
            services.AddSingleton(own);
        }

        return services.BuildServiceProvider();
    }

    private static PolicyCatalog Apply(ServiceProvider host, PolicyCatalog catalog)
    {
        host.GetRequiredService<IPolicyCatalogProvider>().SetCurrent("desk", catalog);
        return catalog;
    }

    private static HandlerRegistration Schedule(ServiceProvider host) =>
        host.GetRequiredService<ExtensionRegistry>().Handlers.Single(handler => handler.Id == Nightly);

    private static IServiceProviderIsService OnlyTheRegistry()
    {
        var services = Substitute.For<IServiceProviderIsService>();
        services.IsService(Arg.Any<Type>()).Returns(call => (Type)call[0] == typeof(ITenantStore));
        return services;
    }

    private static PolicyCatalog Catalog(bool selfService, bool personalTenant = false)
    {
        var descriptor = AlvoDescriptor.Parse(Descriptor(selfService, personalTenant: personalTenant));
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler);
    }

    private const string CronAutomation = """
        , "webhooks": { "endpoints": { "erp": { "url": "https://erp.example/hook", "secretRef": "erp-signing" } } },
          "automation": { "close-day": { "trigger": { "schedule": "0 2 * * *" }, "actions": [ { "type": "webhook", "endpoint": "erp" } ] } }
        """;

    private static string Descriptor(bool selfService, string extra = "", bool personalTenant = false) => $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "desk",
          "auth": { "roles": ["dispatcher"] },
          "tenancy": { "enabled": true{{(selfService ? SelfService(personalTenant) : string.Empty)}} },
          "entities": { "notes": { "tenancy": "scoped", "fields": { "text": { "type": "string", "maxLength": 200 } } } }
          {{extra}}
        }
        """;

    private static string SelfService(bool personalTenant) => personalTenant
        ? """, "selfService": { "assignableRoles": ["dispatcher"], "personalTenant": { "roles": ["dispatcher"] } }"""
        : """, "selfService": { "assignableRoles": ["dispatcher"] }""";

    private sealed class NightlyRoll : IAlvoScheduledJob
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BackfillNotes : IAlvoDataMigration
    {
        public Task RunAsync(AlvoJobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTenants(params TenantId[] tenants) : IAlvoTenantCatalog
    {
        public Task<IReadOnlyList<TenantId>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantId>>(tenants);
    }

    /// <summary>A registry whose only answer is its tenant list — a personal tenant and a shared one.</summary>
    private sealed class ListingRegistry(params StoredTenant[] tenants) : ITenantStore
    {
        public Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StoredTenant>>(tenants);

        public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) => throw new NotSupportedException();

        public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct) => throw new NotSupportedException();

        public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) => throw new NotSupportedException();

        public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) => throw new NotSupportedException();

        public Task<InvitationPreview?> PreviewInvitationAsync(string codeHash, DateTimeOffset now, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AcceptInvitationOutcome> AcceptInvitationAsync(AcceptInvitationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
