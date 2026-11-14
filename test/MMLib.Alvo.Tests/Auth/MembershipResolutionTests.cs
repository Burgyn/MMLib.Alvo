using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;
using NSubstitute;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// Membership resolution (spec §5.7 step 3, S3 plan D4): where a tenant registry is registered, one registry read decided
/// by <see cref="MembershipRules"/>; where none is, no requested tenant is no tenant and any requested tenant refuses the
/// whole principal — never a principal without the tenant it asked for.
/// </summary>
public sealed class MembershipResolutionTests
{
    private static readonly TenantId _tenant = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The membership resolution <c>AddAlvo</c> registers, with <paramref name="store"/> as the registry if any.</summary>
    private static IMembershipResolution Registered(ITenantStore? store = null, string? selfService = null)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        if (store is not null)
        {
            services.AddSingleton(store);
        }

        var provider = services.BuildServiceProvider();
        if (selfService is not null)
        {
            provider.GetRequiredService<IPolicyCatalogProvider>().SetCurrent("tenants", Compile(selfService));
        }

        return provider.GetRequiredService<IMembershipResolution>();
    }

    private static PolicyCatalog Compile(string selfService)
    {
        var descriptor = AlvoDescriptor.Parse($$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "tenants",
              "auth": { "roles": ["dispatcher", "technician"] },
              "tenancy": { "enabled": true, "selfService": {{selfService}} },
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    [Fact]
    public async Task Without_a_tenant_store_the_no_tenant_resolution_stands()
    {
        var resolution = Registered();

        resolution.ShouldBeOfType<NoTenantMembershipResolution>();
        (await resolution.ResolveAsync(UserId.New(), requested: null, Ct)).ShouldBeSameAs(MembershipResolution.NoTenant);
        (await resolution.ResolveAsync(UserId.New(), _tenant, Ct)).ShouldBeSameAs(MembershipResolution.Refusal);
    }

    [Fact]
    public async Task The_registry_resolution_reads_once_and_decides_by_the_rules()
    {
        var user = UserId.New();
        var store = new OneMembershipStore(user, new StoredMembership(
            _tenant, TenantKind.Shared, "Acme", ["dispatcher", "technician"], IsOwner: true, DateTimeOffset.UnixEpoch));
        var resolution = Registered(store, """{ "assignableRoles": ["dispatcher"] }""");

        var named = await resolution.ResolveAsync(user, _tenant, Ct);

        resolution.ShouldBeOfType<RegistryMembershipResolution>();
        named.Refused.ShouldBeFalse();
        named.Tenant.ShouldBe(_tenant);
        named.TenantRoles.Select(role => role.Name).ShouldBe(["dispatcher"], "technician is not assignable");
        store.Reads.ShouldBe([(user, (TenantId?)_tenant)]);
    }

    [Fact]
    public async Task A_tenant_the_person_is_not_in_refuses_the_principal_after_one_read()
    {
        var store = new OneMembershipStore(UserId.New(), null);
        var resolution = Registered(store, """{ "assignableRoles": ["dispatcher"] }""");

        (await resolution.ResolveAsync(UserId.New(), _tenant, Ct)).ShouldBeSameAs(MembershipResolution.Refusal);
        (await resolution.ResolveAsync(UserId.New(), null, Ct)).ShouldBeSameAs(MembershipResolution.NoTenant);
        store.Reads.Count.ShouldBe(1, "no header and no personal tenant reads nothing");
    }

    [Fact]
    public async Task Without_self_service_the_registry_is_never_read()
    {
        var store = new OneMembershipStore(UserId.New(), null);
        var resolution = Registered(store);

        (await resolution.ResolveAsync(UserId.New(), _tenant, Ct)).ShouldBeSameAs(MembershipResolution.Refusal);
        (await resolution.ResolveAsync(UserId.New(), null, Ct)).ShouldBeSameAs(MembershipResolution.NoTenant);
        store.Reads.ShouldBeEmpty();
    }

    /// <summary>A registry holding one membership of one person, recording every membership read.</summary>
    private sealed class OneMembershipStore(UserId member, StoredMembership? membership) : ITenantStore
    {
        internal List<(UserId User, TenantId? Tenant)> Reads { get; } = [];

        public Task<StoredMembership?> FindMembershipAsync(UserId user, TenantId? tenant, CancellationToken ct)
        {
            Reads.Add((user, tenant));
            return Task.FromResult(user == member && membership?.Tenant == tenant ? membership : null);
        }

        public Task<CreateTenantOutcome> CreateSharedAsync(CreateTenantRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StoredMembership>> ListMembershipsAsync(UserId user, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StoredTenant>> ListTenantsAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<MemberChangeStatus> RenameAsync(TenantId tenant, UserId actor, string name, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MemberView>?> ListMembersAsync(TenantId tenant, UserId actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MemberChangeStatus> UpdateMemberAsync(MemberUpdate update, CancellationToken ct) => throw new NotSupportedException();

        public Task<MemberChangeStatus> RemoveMemberAsync(MemberRemoval removal, CancellationToken ct) => throw new NotSupportedException();

        public Task<CreateInvitationOutcome> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(TenantId tenant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<MemberChangeStatus> RevokeInvitationAsync(InvitationRevocation revocation, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
