using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// Resolving an app token (S5 plan D5, D6, D11): a personal token acts as its person with the tenant roles of their
/// <em>current</em> membership, a service token as itself with its stored roles still assignable; a kind the applied
/// descriptor does not turn on, a revoked or expired token, a wrong secret and a membership that ended are each a refusal
/// of the whole principal.
/// </summary>
public sealed class AppTokenResolutionTests
{
    private const string Secret = "app-token-secret-value";
    private const string PersonalId = AppTokenSecret.PersonalPrefix + "PPPPPPPPPPPPPPPPPPPPPP";
    private const string ServiceId = AppTokenSecret.ServicePrefix + "SSSSSSSSSSSSSSSSSSSSSS";

    private const string AllOn = """
        { "personalTenant": { "roles": ["dispatcher"] }, "createTenants": { "creatorRoles": ["dispatcher"] },
          "assignableRoles": ["dispatcher", "technician"] }
        """;

    private static readonly TenantId _tenant = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly TenantId _personal = new(Guid.Parse("22222222-2222-4222-8222-222222222222"));
    private static readonly UserId _person = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    private static readonly UserId _service = new(Guid.Parse("44444444-4444-4444-8444-444444444444"));

    private readonly RecordingAppTokenStore _registry = new();
    private readonly RecordingMembershipStore _tenants = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-14T12:00:00Z", null));
    private readonly PolicyCatalogProvider _catalogs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PolicyCatalog Compile(string? accessTokens, string? selfService)
    {
        var descriptor = AlvoDescriptor.Parse($$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "tokens",
              "auth": {
                "providers": ["google"],
                "roles": ["dispatcher", "technician"],
                "signUp": { "mode": "open" }
                {{(accessTokens is null ? string.Empty : $", \"accessTokens\": {accessTokens}")}}
              },
              "tenancy": { "enabled": true {{(selfService is null ? string.Empty : $", \"selfService\": {selfService}")}} },
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """);
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    private void Apply(string? accessTokens = """{ "personal": true, "service": true }""", string? selfService = AllOn) =>
        _catalogs.SetCurrent("tokens", Compile(accessTokens, selfService));

    private ApiKeyContextResolver Resolver()
    {
        var devKeys = new InMemoryApiKeyStore(Options.Create(new AlvoAuthOptions()));
        var memberships = new RegistryMembershipResolution(_tenants, _catalogs);
        return new ApiKeyContextResolver(
            new ApiKeyStores(devKeys, _registry, _clock),
            RoleCatalog.BuiltInOnly,
            _clock,
            new TenantResolver(),
            _catalogs,
            new AppTokenPrincipals(memberships, _catalogs));
    }

    private ApiKeyRecord Stored(string keyId, TenantId? tenant, IReadOnlyList<string>? roles = null) => Stored(new ApiKeyRecord
    {
        KeyId = keyId,
        Sha256Hash = ApiKeyHash.Compute(Secret),
        User = keyId == PersonalId ? _person : _service,
        RoleNames = roles ?? [],
        Tenant = tenant,
        Scopes = new HashSet<ApiKeyScope>(),
        ExpiresAt = _clock.GetUtcNow().AddDays(30),
        Kind = AppTokenSecret.KindOf(keyId),
        MintedBy = _person,
    });

    private ApiKeyRecord Stored(ApiKeyRecord token) => _registry.Put(token);

    private void Member(TenantId tenant, TenantKind kind, params string[] roles) =>
        _tenants.Set(_person, new StoredMembership(tenant, kind, "Acme", roles, IsOwner: true, DateTimeOffset.UnixEpoch));

    private static string[] Names(IReadOnlySet<Role> roles) => roles.Select(role => role.Name).Order().ToArray();

    private Task<AlvoPrincipal?> Present(string keyId, string? header = null, string secret = Secret) =>
        Resolver().ResolveAsync(keyId + "." + secret, header, Ct).AsTask();

    [Fact]
    public async Task A_personal_token_acts_as_its_person_with_live_tenant_roles()
    {
        Apply();
        Stored(PersonalId, _tenant);
        var resolver = Resolver();
        Member(_tenant, TenantKind.Shared, "dispatcher");
        var first = await resolver.ResolveAsync(PersonalId + "." + Secret, null, Ct);
        Member(_tenant, TenantKind.Shared, "technician");

        var second = await resolver.ResolveAsync(PersonalId + "." + Secret, null, Ct);

        Names(first.ShouldNotBeNull().Context.TenantRoles).ShouldBe(["dispatcher"]);
        Names(second.ShouldNotBeNull().Context.TenantRoles).ShouldBe(["technician"], "the membership is read live");
        second.Context.ActorKind.ShouldBe(AlvoActorKind.User);
        second.Context.User.ShouldBe(_person);
        second.Context.Roles.ShouldBe([Role.Authenticated]);
        second.Context.Tenant.ShouldBe(_tenant);
        second.KeyId.ShouldBe(PersonalId);
    }

    [Fact]
    public async Task A_tenantless_personal_token_acts_in_no_tenant_and_reads_no_membership()
    {
        Apply();
        Stored(PersonalId, tenant: null);

        var principal = await Present(PersonalId);

        principal.ShouldNotBeNull().Context.Tenant.ShouldBeNull();
        principal.Context.TenantRoles.ShouldBeEmpty();
        principal.Context.ActorKind.ShouldBe(AlvoActorKind.User);
        _tenants.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_personal_token_whose_membership_ended_is_refused()
    {
        Apply();
        Stored(PersonalId, _tenant);

        (await Present(PersonalId)).ShouldBeNull();
        _registry.Touches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_personal_token_in_a_personal_tenant_holds_the_personal_roles()
    {
        Apply();
        Stored(PersonalId, _personal);
        Member(_personal, TenantKind.Personal, "technician");

        var principal = await Present(PersonalId);

        Names(principal.ShouldNotBeNull().Context.TenantRoles).ShouldBe(["dispatcher"], "personalTenant.roles, not the row's");
        principal.Context.Tenant.ShouldBe(_personal);
    }

    [Fact]
    public async Task A_service_token_acts_as_itself_with_its_roles_still_assignable()
    {
        Apply(selfService: """{ "createTenants": { "creatorRoles": ["dispatcher"] }, "assignableRoles": ["dispatcher"] }""");
        Stored(ServiceId, _tenant, ["dispatcher", "technician"]);

        var principal = await Present(ServiceId);

        principal.ShouldNotBeNull().Context.ActorKind.ShouldBe(AlvoActorKind.ApiKey);
        principal.Context.User.ShouldBe(_service);
        principal.Context.Roles.ShouldBe([Role.Authenticated]);
        principal.Context.Tenant.ShouldBe(_tenant);
        Names(principal.Context.TenantRoles).ShouldBe(["dispatcher"], "technician is no longer assignable");
        _tenants.Reads.ShouldBeEmpty();
        _registry.Touches.ShouldBe([(ServiceId, _clock.GetUtcNow())]);
    }

    [Theory]
    [InlineData(PersonalId)]
    [InlineData(ServiceId)]
    public async Task A_header_naming_another_tenant_refuses_either_kind(string keyId)
    {
        Apply();
        Stored(keyId, _tenant, ["dispatcher"]);
        Member(_personal, TenantKind.Shared, "dispatcher");

        (await Present(keyId, _personal.ToString())).ShouldBeNull();
        _tenants.Reads.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PersonalId)]
    [InlineData(ServiceId)]
    public async Task A_header_naming_its_own_tenant_confirms_it(string keyId)
    {
        Apply();
        Stored(keyId, _tenant, ["dispatcher"]);
        Member(_tenant, TenantKind.Shared, "dispatcher");

        var principal = await Present(keyId, _tenant.ToString());

        principal.ShouldNotBeNull().Context.Tenant.ShouldBe(_tenant);
        Names(principal.Context.TenantRoles).ShouldBe(["dispatcher"]);
    }

    [Theory]
    [InlineData(PersonalId, null, AllOn)]
    [InlineData(PersonalId, """{ "personal": false, "service": true }""", AllOn)]
    [InlineData(ServiceId, """{ "personal": true, "service": false }""", AllOn)]
    [InlineData(ServiceId, """{ "personal": true, "service": true }""", null)]
    public async Task A_kind_the_descriptor_does_not_enable_is_refused(string keyId, string? accessTokens, string? selfService)
    {
        Apply(accessTokens, selfService);
        Stored(keyId, _tenant, ["dispatcher"]);
        Member(_tenant, TenantKind.Shared, "dispatcher");

        (await Present(keyId)).ShouldBeNull();
        _tenants.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Re_enabling_revives_an_unexpired_token()
    {
        Stored(PersonalId, _tenant);
        Member(_tenant, TenantKind.Shared, "dispatcher");
        Apply(accessTokens: """{ "personal": false }""");
        (await Present(PersonalId)).ShouldBeNull();

        Apply();

        (await Present(PersonalId)).ShouldNotBeNull().Context.Tenant.ShouldBe(_tenant);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_expired_or_revoked_token_is_refused_before_any_membership_read(bool revoked)
    {
        Apply();
        var token = Stored(PersonalId, _tenant);
        Stored(revoked
            ? token with { RevokedAt = _clock.GetUtcNow().AddMinutes(-1) }
            : token with { ExpiresAt = _clock.GetUtcNow() });
        Member(_tenant, TenantKind.Shared, "dispatcher");

        (await Present(PersonalId)).ShouldBeNull();
        _tenants.Reads.ShouldBeEmpty();
        _registry.Touches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_wrong_secret_is_refused_like_an_unknown_id()
    {
        Apply();
        Stored(PersonalId, _tenant);
        Member(_tenant, TenantKind.Shared, "dispatcher");
        const string UnknownId = AppTokenSecret.PersonalPrefix + "UUUUUUUUUUUUUUUUUUUUUU";

        (await Present(PersonalId, secret: "wrong-secret")).ShouldBeNull();
        (await Present(UnknownId)).ShouldBeNull();

        _registry.Finds.ShouldBe([PersonalId, UnknownId]);
        _tenants.Reads.ShouldBeEmpty();
        _registry.Touches.ShouldBeEmpty();
    }
}
