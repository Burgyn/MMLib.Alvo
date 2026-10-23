using MMLib.Alvo.Data;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;

using Shouldly;

using Xunit;

using DescField = MMLib.Alvo.Descriptor.FieldType;
using SchemaField = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Testing.Events;

/// <summary>
/// The two-tenant adversarial fact for after-hooks (spec 2026-10-14 §6.2, HX-01 acceptance 6): a condition comparing a
/// row's tenant with <c>@tenant.id</c> is decided by the envelope's own <c>tenantid</c>, on a real write through a real
/// dispatcher, so a hook meant to catch "another tenant's row" never fires for a tenant's own writes.
/// </summary>
/// <remarks>
/// <b>The fact carries its own mirror.</b> "The cross-tenant hook never fired" passes just as well when
/// <c>@tenant.id</c> never resolved and every hook reading it was skipped, so the same entity declares the
/// opposite condition, and the fact demands it fire once per tenant. The subclass supplies a started database and
/// nothing else, so both shipped engines are asked the same question.
/// </remarks>
public abstract class AlvoEventTenancyTests
{
    /// <summary>Builds a fresh backend over <paramref name="project"/>.</summary>
    /// <param name="project">The schema and descriptor the backend is started over.</param>
    protected abstract Task<IAlvoEventWorld> WorldAsync(AlvoEventProject project);

    /// <summary>
    /// Two tenants each write one row of their own: the cross-tenant condition fires for neither, and its mirror
    /// fires for both.
    /// </summary>
    [Fact]
    public async Task A_cross_tenant_condition_never_fires_and_its_mirror_fires_for_each_tenant()
    {
        await using var world = await WorldAsync(Project);
        var acme = Caller(TenantId.New());
        var globex = Caller(TenantId.New());

        await CreateAsync(world, acme);
        await CreateAsync(world, globex);
        await world.DrainAsync();

        world.Deliveries.Where(delivery => delivery.Url == LeakUrl).ShouldBeEmpty(
            "new.tenant_id != @tenant.id must be false for every write a tenant makes to its own rows");
        world.Deliveries.Count(delivery => delivery.Url == OwnUrl).ShouldBe(
            2, "the mirror condition proves @tenant.id resolved at all — without it 'never fires' is vacuous");
    }

    /// <summary>The project the fact is measured on: one scoped entity and the two mirrored after-hooks.</summary>
    protected static AlvoEventProject Project => new(Schema, Descriptor, Caller(TenantId.New()), Tickets, "MMLib.Alvo.Events");

    private const string Tickets = "tickets";
    private const string OwnUrl = "https://receiver.test/own-tenant";
    private const string LeakUrl = "https://receiver.test/cross-tenant";
    private const string AuthenticatedOnly = "'authenticated' in @user.roles";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<AlvoRecord> CreateAsync(IAlvoEventWorld world, AlvoContext caller) =>
        world.Data.CreateAsync(Tickets, Ticket(caller.Tenant!.Value), caller, cancellationToken: Ct);

    private static Dictionary<string, object?> Ticket(TenantId tenant) => new(StringComparer.Ordinal)
    {
        ["title"] = "printer on fire",
        [AlvoManagedColumns.TenantId] = tenant.Value,
    };

    private static AlvoContext Caller(TenantId tenant) => new()
    {
        User = UserId.New(),
        Roles = new HashSet<Role> { Role.Authenticated },
        Tenant = tenant,
    };

    private static AlvoDescriptor Descriptor => new()
    {
        ApiVersion = "alvo.dev/v1",
        Name = "event-tenancy-suite",
        Entities = new Dictionary<string, EntityDescriptor>(StringComparer.Ordinal)
        {
            [Tickets] = new()
            {
                Tenancy = EntityTenancy.Scoped,
                Fields = new Dictionary<string, FieldDescriptor>(StringComparer.Ordinal)
                {
                    ["title"] = new() { Type = DescField.String },
                },
                Rules = new AccessRules
                {
                    List = AuthenticatedOnly,
                    Get = AuthenticatedOnly,
                    Create = AuthenticatedOnly,
                    Update = AuthenticatedOnly,
                    Delete = AuthenticatedOnly,
                },
                Hooks = new EntityHooks
                {
                    AfterCreate =
                    [
                        new AfterHook { Condition = "new.tenant_id != @tenant.id", Action = new WebhookAction { Endpoint = "leak" } },
                        new AfterHook { Condition = "new.tenant_id == @tenant.id", Action = new WebhookAction { Endpoint = "own" } },
                    ],
                },
            },
        },
        Webhooks = new Webhooks
        {
            Endpoints = new Dictionary<string, WebhookEndpoint>(StringComparer.Ordinal)
            {
                ["leak"] = new() { Url = LeakUrl, SecretRef = "leak-secret" },
                ["own"] = new() { Url = OwnUrl, SecretRef = "own-secret" },
            },
        },
    };

    private static SchemaModel Schema => new([
        new EntitySchema
        {
            Name = Tickets,
            Tenancy = TenancyMode.Scoped,
            Fields =
            [
                new FieldSchema { Name = AlvoManagedColumns.Id, Type = SchemaField.Uuid, Required = true },
                new FieldSchema { Name = "title", Type = SchemaField.String, Nullable = true },
                new FieldSchema { Name = AlvoManagedColumns.TenantId, Type = SchemaField.Uuid, Required = true, Indexed = true },
            ],
        },
    ]);
}
