using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Expressions;
using MMLib.Alvo.Tests.Extensions;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// An <c>EventActor</c> run's tenant roles (S3 plan D10b): the recorded, still assignable ones narrowed to the membership
/// the person holds in the event's tenant now — removed, none; demoted, what is still held; never one gained since. Project
/// roles stay as at the event, and only a person with a tenant and tenant roles costs a membership read.
/// </summary>
public sealed class DeliveryRunnerActorTests
{
    private const string Handler = "on-created";

    private static readonly TenantId _tenant = new(Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002"));
    private static readonly UserId _person = new(Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001"));
    private static readonly PolicyCatalog _catalog = SelfServiceCatalog();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_event_actor_delivery_of_a_removed_member_runs_without_tenant_roles()
    {
        var memberships = new CountingMemberships(MembershipResolution.Refusal);

        var caller = await CallerAsync(memberships, Event(["dispatcher"]));

        caller.TenantRoles.ShouldBeEmpty();
        (caller.User, caller.Tenant).ShouldBe((_person, (TenantId?)_tenant));
        caller.Roles.Select(role => role.Name).Order(StringComparer.Ordinal).ShouldBe(["auditor", "authenticated"]);
        memberships.Reads.ShouldBe([(_person, (TenantId?)_tenant)]);
    }

    [Fact]
    public async Task An_event_actor_delivery_of_a_demoted_member_keeps_only_the_roles_still_held()
    {
        var memberships = new CountingMemberships(Holding("technician"));

        var caller = await CallerAsync(memberships, Event(["dispatcher", "technician"]));

        caller.TenantRoles.Select(role => role.Name).ShouldBe(["technician"]);
    }

    [Fact]
    public async Task A_membership_widened_since_the_event_widens_nothing()
    {
        var memberships = new CountingMemberships(Holding("dispatcher", "technician"));

        var caller = await CallerAsync(memberships, Event(["technician"]));

        caller.TenantRoles.Select(role => role.Name).ShouldBe(["technician"]);
    }

    [Fact]
    public async Task An_api_key_actor_and_a_system_run_read_no_membership()
    {
        var memberships = new CountingMemberships(MembershipResolution.Refusal);

        var apiKey = await CallerAsync(memberships, Event(["dispatcher"]) with { AuthType = AlvoEventAuthType.ApiKey });
        var system = await CallerAsync(memberships, Event(["dispatcher"]), AlvoRunAs.System);
        var noTenantRoles = await CallerAsync(memberships, Event([]));

        (apiKey.ActorKind, system.ActorKind, noTenantRoles.ActorKind).ShouldBe((AlvoActorKind.ApiKey, AlvoActorKind.System, AlvoActorKind.User));
        memberships.Reads.ShouldBeEmpty();
    }

    /// <summary>The caller one run of <paramref name="runAs"/> hands its handler for <paramref name="event"/>.</summary>
    private static async Task<AlvoContext> CallerAsync(IMembershipResolution memberships, AlvoEvent @event, AlvoRunAs runAs = AlvoRunAs.EventActor)
    {
        AlvoContext? caller = null;
        var registry = HandlerCatalog.Registry(alvo => alvo.Entity("tickets", t => t.OnCreated(
            Handler,
            (AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            {
                caller = context.Caller;
                return Task.CompletedTask;
            },
            configure: options => options.RunAs = runAs)));
        var binder = new ExtensionBinder(registry, CelFixtures.Compiler, HandlerCatalog.EveryService(), NullLogger<ExtensionBinder>.Instance);

        await Runner(memberships).InvokeOnceAsync(@event, binder.For(_catalog).Handler(Handler).ShouldNotBeNull(), attempt: 1, Ct);

        return caller.ShouldNotBeNull();
    }

    private static DeliveryRunner Runner(IMembershipResolution memberships)
    {
        var catalogs = new PolicyCatalogProvider();
        catalogs.SetCurrent("handlers", _catalog);
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));

        return new DeliveryRunner(
            new FakeDeliveryStore(clock),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new AlvoSessions(Substitute.For<IAlvoData>()),
            catalogs,
            memberships,
            new DeliveryBackoff(() => 0.5),
            new AbandonedHandlers(Options.Create(new AlvoDeliveryOptions()), NullLogger<AbandonedHandlers>.Instance),
            clock,
            NullLogger<DeliveryRunner>.Instance);
    }

    private static AlvoEvent Event(string[] tenantRoles) => new()
    {
        Id = AlvoEventId.Create(DateTimeOffset.UtcNow),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.User,
        AuthId = _person.ToString(),
        AuthClaims = AlvoEventProvenance.AuthClaimsOf(new AlvoContext
        {
            User = _person,
            Tenant = _tenant,
            Roles = _catalog.Roles.Resolve(["authenticated", "auditor"]),
            TenantRoles = _catalog.Roles.Resolve(tenantRoles),
        }),
        TenantId = _tenant.ToString(),
        CorrelationId = "c",
        Data = new AlvoEventData { Record = AlvoRecord.Empty },
    };

    private static MembershipResolution Holding(params string[] roles) => new(false, _tenant, _catalog.Roles.Resolve(roles));

    /// <summary>A scoped <c>tickets</c> in a self-service project: <c>auditor</c> is a project role, the other two assignable.</summary>
    private static PolicyCatalog SelfServiceCatalog()
    {
        var descriptor = AlvoDescriptor.Parse("""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "handlers",
              "auth": { "roles": ["dispatcher", "technician", "auditor"] },
              "tenancy": { "enabled": true, "selfService": { "assignableRoles": ["dispatcher", "technician"] } },
              "entities": { "tickets": { "tenancy": "scoped", "fields": { "status": { "type": "string", "maxLength": 20 } } } }
            }
            """);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), CelFixtures.Compiler);
    }

    /// <summary>Answers every membership read with one resolution, recording each read.</summary>
    private sealed class CountingMemberships(MembershipResolution answer) : IMembershipResolution
    {
        internal List<(UserId User, TenantId? Tenant)> Reads { get; } = [];

        public ValueTask<MembershipResolution> ResolveAsync(UserId user, TenantId? requested, CancellationToken ct)
        {
            Reads.Add((user, requested));
            return ValueTask.FromResult(answer);
        }
    }
}
