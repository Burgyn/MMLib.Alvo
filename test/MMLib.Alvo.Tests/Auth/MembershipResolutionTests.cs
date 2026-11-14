using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// Membership resolution before tenants exist (S2 D13): no requested tenant is a caller with no tenant, and any requested
/// tenant refuses the whole principal — never a principal without the tenant it asked for.
/// </summary>
public sealed class MembershipResolutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The membership resolution <c>AddAlvo</c> registers.</summary>
    private static IMembershipResolution Registered()
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        return services.BuildServiceProvider().GetRequiredService<IMembershipResolution>();
    }

    [Fact]
    public async Task No_requested_tenant_is_no_tenant()
    {
        var resolution = await Registered().ResolveAsync(UserId.New(), requested: null, Ct);

        resolution.ShouldBeSameAs(MembershipResolution.NoTenant);
        resolution.Refused.ShouldBeFalse();
        resolution.Tenant.ShouldBeNull();
        resolution.TenantRoles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Any_requested_tenant_is_refused_in_this_build()
    {
        var resolution = await Registered().ResolveAsync(UserId.New(), new TenantId(Guid.NewGuid()), Ct);

        resolution.ShouldBeSameAs(MembershipResolution.Refusal);
        resolution.Refused.ShouldBeTrue();
        resolution.Tenant.ShouldBeNull();
        resolution.TenantRoles.ShouldBeEmpty();
    }
}
