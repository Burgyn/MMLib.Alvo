using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Accounts;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Tenants;
using Shouldly;
using System.Data.Common;
using System.Globalization;
using Xunit;

#pragma warning disable EF1001

namespace MMLib.Alvo.Tests.Ef;

/// <summary>
/// The tenant registry — tenants, memberships, the creator's cap — the same facts on SQLite and on PostgreSQL (accounts
/// spec §6.1, §6.4; S3 plan D2, D3, D9, D15). Accounts are created through <see cref="IAccountStore.SignInAsync"/>; members
/// and personal tenants no port member writes yet are seeded with <see cref="TenantSeed"/>.
/// </summary>
public abstract class TenantStoreFacts : IAsyncDisposable
{
    private const int Iterations = 20;

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] _tenantTables = ["alvo_tenants", "alvo_tenant_members"];

    /// <summary>Gets the instant every fact starts at.</summary>
    protected static DateTimeOffset Start => _now;

    /// <summary>Gets the running fact's cancellation token.</summary>
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts one fresh database and returns its host container.</summary>
    protected abstract Task<IServiceProvider> StartAsync();

    /// <summary>Disposes the engine fixture.</summary>
    protected abstract ValueTask DisposeFixtureAsync();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisposeFixtureAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Creating_a_tenant_makes_the_creator_its_owner_with_the_creator_roles()
    {
        var (store, services) = await StoreAsync();
        var creator = await SignedUpAsync(services);
        var tenant = TenantId.New();

        var outcome = await store.CreateSharedAsync(Create(tenant, creator, ["dispatcher", "planner"]), Ct);

        var expected = new StoredMembership(tenant, TenantKind.Shared, "Acme", ["dispatcher", "planner"], IsOwner: true, _now);
        outcome.Status.ShouldBe(CreateTenantStatus.Created);
        Same(outcome.Membership.ShouldNotBeNull(), expected);
        Same((await store.FindMembershipAsync(creator, tenant, Ct)).ShouldNotBeNull(), expected);
        (await store.ListMembershipsAsync(creator, Ct)).Count.ShouldBe(1);
        (await VersionAsync(services, tenant)).ShouldBe(0, "a new tenant starts at version 0");
    }

    [Fact]
    public async Task Memberships_list_the_personal_tenant_first_then_by_joining()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var (early, laterHigh, laterLow, joined, personal) = (Tenant(1), Tenant(0xF0), Tenant(0xE0), Tenant(5), Tenant(9));
        await store.CreateSharedAsync(Create(early, person, [], _now.AddMinutes(1)), Ct);
        await store.CreateSharedAsync(Create(laterHigh, person, [], _now.AddMinutes(3)), Ct);
        await store.CreateSharedAsync(Create(laterLow, person, [], _now.AddMinutes(3)), Ct);
        await store.CreateSharedAsync(Create(joined, other, [], _now), Ct);
        await TenantSeed.AddMemberAsync(services, joined, person, ["viewer"], isOwner: false, _now.AddMinutes(2));
        await TenantSeed.AddPersonalTenantAsync(services, personal, person, "Ada", _now.AddMinutes(4));

        var memberships = await store.ListMembershipsAsync(person, Ct);

        memberships.Select(membership => membership.Tenant).ShouldBe([personal, early, joined, laterLow, laterHigh]);
        memberships[0].Kind.ShouldBe(TenantKind.Personal);
        memberships[0].Roles.ShouldBeEmpty();
        memberships[2].IsOwner.ShouldBeFalse("a membership in someone else's tenant is not an ownership");
        memberships[2].Roles.ShouldBe(["viewer"]);
    }

    [Fact]
    public async Task Find_answers_a_membership_only_to_its_member()
    {
        var (store, services) = await StoreAsync();
        var owner = await SignedUpAsync(services);
        var stranger = await SignedUpAsync(services);
        var tenant = TenantId.New();
        await store.CreateSharedAsync(Create(tenant, owner, []), Ct);

        (await store.FindMembershipAsync(stranger, tenant, Ct)).ShouldBeNull("another person is no member");
        (await store.FindMembershipAsync(owner, TenantId.New(), Ct)).ShouldBeNull("an unknown tenant has no members");
        (await store.FindMembershipAsync(owner, null, Ct)).ShouldBeNull("no personal tenant yet");

        var personal = TenantId.New();
        await TenantSeed.AddPersonalTenantAsync(services, personal, owner, "Ada", _now);
        await TenantSeed.AddPersonalTenantAsync(services, TenantId.New(), stranger, "Bob", _now);

        var found = (await store.FindMembershipAsync(owner, null, Ct)).ShouldNotBeNull();
        found.Tenant.ShouldBe(personal, "the personal tenant is the owner's own, never another's");
        found.Kind.ShouldBe(TenantKind.Personal);
        found.Name.ShouldBe("Ada");
        (await store.FindMembershipAsync(stranger, personal, Ct)).ShouldBeNull("a personal tenant is its owner's alone");
    }

    [Fact]
    public async Task The_cap_counts_the_shared_tenants_the_account_created()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var foreign = TenantId.New();
        await store.CreateSharedAsync(Create(foreign, other, [], cap: 2), Ct);
        await TenantSeed.AddMemberAsync(services, foreign, person, [], isOwner: true, _now);
        await TenantSeed.AddPersonalTenantAsync(services, TenantId.New(), person, "Ada", _now);

        var first = await store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 2), Ct);
        var second = await store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 2), Ct);
        var refused = TenantId.New();
        var third = await store.CreateSharedAsync(Create(refused, person, [], cap: 2), Ct);

        first.Status.ShouldBe(CreateTenantStatus.Created, "a membership or a personal tenant is not a creation");
        second.Status.ShouldBe(CreateTenantStatus.Created);
        third.ShouldBe(new CreateTenantOutcome(CreateTenantStatus.LimitReached, null));
        (await store.FindMembershipAsync(person, refused, Ct)).ShouldBeNull("a refusal writes nothing");
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(4);
        (await store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 3), Ct)).Status
            .ShouldBe(CreateTenantStatus.Created, "the cap is the request's, so a raised one admits another");
    }

    [Fact]
    public async Task Concurrent_creates_at_the_cap_never_exceed_it()
    {
        var (store, services) = await StoreAsync();

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var person = await SignedUpAsync(services);

            var outcomes = await Task.WhenAll(
                Enumerable.Range(0, 5).Select(_ => store.CreateSharedAsync(Create(TenantId.New(), person, [], cap: 2), Ct)));

            outcomes.Count(outcome => outcome.Status == CreateTenantStatus.Created).ShouldBe(2, $"iteration {iteration}");
            outcomes.Count(outcome => outcome.Status == CreateTenantStatus.LimitReached).ShouldBe(3, $"iteration {iteration}");
            (await store.ListMembershipsAsync(person, Ct)).Count.ShouldBe(2, $"iteration {iteration}");
        }

        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(2 * Iterations);
    }

    [Fact]
    public async Task A_disabled_or_unknown_creator_creates_nothing()
    {
        var (store, services) = await StoreAsync();
        var disabled = await SignedUpAsync(services);
        (await services.GetRequiredService<IAccountStore>().SetDisabledAsync(disabled, disabled: true, _now, Ct)).ShouldBeTrue();

        var unknown = await store.CreateSharedAsync(Create(TenantId.New(), UserId.New(), []), Ct);
        var banned = await store.CreateSharedAsync(Create(TenantId.New(), disabled, []), Ct);

        unknown.ShouldBe(new CreateTenantOutcome(CreateTenantStatus.NoAccount, null));
        banned.ShouldBe(new CreateTenantOutcome(CreateTenantStatus.NoAccount, null));
        (await CountAsync(services, AlvoFrameworkTables.TenantsSuffix)).ShouldBe(0);
        (await CountAsync(services, AlvoFrameworkTables.TenantMembersSuffix)).ShouldBe(0);
    }

    [Fact]
    public async Task Tenant_ids_list_every_tenant_once()
    {
        var (store, services) = await StoreAsync();
        var person = await SignedUpAsync(services);
        var other = await SignedUpAsync(services);
        var (first, tiedHigh, tiedLow, personal) = (Tenant(1), Tenant(0xF0), Tenant(0xE0), Tenant(7));
        await store.CreateSharedAsync(Create(tiedHigh, person, [], _now.AddMinutes(2)), Ct);
        await store.CreateSharedAsync(Create(first, other, [], _now.AddMinutes(1)), Ct);
        await store.CreateSharedAsync(Create(tiedLow, other, [], _now.AddMinutes(2)), Ct);
        await TenantSeed.AddMemberAsync(services, first, person, [], isOwner: false, _now.AddMinutes(5));
        await TenantSeed.AddPersonalTenantAsync(services, personal, person, "Ada", _now.AddMinutes(3));

        (await store.ListTenantIdsAsync(Ct)).ShouldBe([first, tiedLow, tiedHigh, personal]);
    }

    [Fact]
    public void The_tables_are_reserved_framework_names() =>
        _tenantTables.Except(AlvoFrameworkTables.NamesFor("alvo"), StringComparer.Ordinal).ShouldBeEmpty();

    /// <summary>Starts a fresh database and returns its tenant store and container.</summary>
    private protected async Task<(ITenantStore Store, IServiceProvider Services)> StoreAsync()
    {
        var services = await StartAsync();
        return (services.GetRequiredService<ITenantStore>(), services);
    }

    /// <summary>Signs a fresh identity up and returns its account.</summary>
    /// <param name="services">The host container.</param>
    /// <param name="name">The identity's display name.</param>
    /// <param name="picture">The identity's avatar URL.</param>
    private protected static async Task<UserId> SignedUpAsync(IServiceProvider services, string? name = "Ada", string? picture = null)
    {
        var identity = new VerifiedIdentity("google", "sub-" + Guid.NewGuid().ToString("N"), "ada@gmail.com", true, name, picture);
        var request = new SignInRequest(
            identity, MayCreate: true, Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), _now, _now.AddDays(180));
        var outcome = await services.GetRequiredService<IAccountStore>().SignInAsync(request, Ct);
        return outcome.Account!.Value;
    }

    /// <summary>A creation of a shared tenant named <c>Acme</c>.</summary>
    private protected static CreateTenantRequest Create(
        TenantId tenant, UserId creator, IReadOnlyList<string> roles, DateTimeOffset? now = null, int cap = 5) =>
        new(tenant, "Acme", creator, roles, cap, now ?? _now);

    /// <summary>A tenant id whose text sorts by <paramref name="order"/> — for the facts that pin a tie-break by id.</summary>
    private protected static TenantId Tenant(byte order) =>
        new(Guid.Parse(order.ToString("x2", CultureInfo.InvariantCulture) + "000000-0000-4000-8000-000000000000", CultureInfo.InvariantCulture));

    /// <summary>Asserts two memberships are equal, roles compared by value.</summary>
    private protected static void Same(StoredMembership actual, StoredMembership expected)
    {
        actual.ShouldBe(expected with { Roles = actual.Roles });
        actual.Roles.ShouldBe(expected.Roles);
    }

    /// <summary>The prefixed name of the framework table <paramref name="suffix"/> names.</summary>
    protected static string TableOf(IServiceProvider services, string suffix) =>
        services.GetRequiredService<IOptions<AlvoOptions>>().Value.SchemaPrefix + suffix;

    /// <summary>How many rows the framework table <paramref name="suffix"/> names holds.</summary>
    protected static async Task<long> CountAsync(IServiceProvider services, string suffix) =>
        Convert.ToInt64(await ScalarAsync(services, $"SELECT COUNT(*) FROM {TableOf(services, suffix)}"), CultureInfo.InvariantCulture);

    /// <summary>The stored <c>version</c> of <paramref name="tenant"/>.</summary>
    protected static async Task<long> VersionAsync(IServiceProvider services, TenantId tenant) => Convert.ToInt64(
        await ScalarAsync(services, $"SELECT version FROM {TableOf(services, AlvoFrameworkTables.TenantsSuffix)} WHERE id = '{tenant.Value:D}'"),
        CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="sql"/> on its own connection and returns the first column of the first row.</summary>
    protected static async Task<object?> ScalarAsync(IServiceProvider services, string sql)
    {
        await using var connection = services.GetRequiredService<RelationalConnectionFactory>().Create();
        await connection.OpenAsync(Ct);
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }
}
