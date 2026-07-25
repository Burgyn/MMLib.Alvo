using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Tests.Auth;

public class TenantResolverTests
{
    private static readonly TenantId _tenant = TenantId.New();

    private static ApiKeyRecord KeyWithTenant(TenantId? tenant) => new()
    {
        KeyId = "dev",
        Sha256Hash = "hash",
        User = UserId.New(),
        RoleNames = ["authenticated"],
        Tenant = tenant,
        Scopes = new HashSet<ApiKeyScope>(),
    };

    [Fact]
    public void The_keys_own_tenant_wins_when_none_is_requested()
    {
        new TenantResolver().TryResolve(KeyWithTenant(_tenant), requestedTenant: null, out var tenant).ShouldBeTrue();

        tenant.ShouldBe(_tenant);
    }

    [Fact]
    public void A_matching_requested_tenant_resolves_to_that_tenant()
    {
        new TenantResolver().TryResolve(KeyWithTenant(_tenant), _tenant.ToString(), out var tenant).ShouldBeTrue();

        tenant.ShouldBe(_tenant);
    }

    [Fact]
    public void A_differing_requested_tenant_denies()
    {
        new TenantResolver().TryResolve(KeyWithTenant(_tenant), TenantId.New().ToString(), out var tenant).ShouldBeFalse();

        tenant.ShouldBeNull();
    }

    [Fact]
    public void A_malformed_requested_tenant_denies()
    {
        new TenantResolver().TryResolve(KeyWithTenant(_tenant), "not-a-guid", out var tenant).ShouldBeFalse();

        tenant.ShouldBeNull();
    }

    [Fact]
    public void No_key_tenant_and_no_request_resolves_to_null_and_still_succeeds()
    {
        new TenantResolver().TryResolve(KeyWithTenant(null), requestedTenant: null, out var tenant).ShouldBeTrue();

        tenant.ShouldBeNull();
    }
}
