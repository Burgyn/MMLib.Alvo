using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Tests.Auth;

public class AlvoAuthSetupTests
{
    [Fact]
    public void AddAlvo_resolves_the_dev_auth_services()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAlvoContextResolver>().ShouldNotBeNull();
        provider.GetRequiredService<ScopeGate>().ShouldNotBeNull();
        provider.GetRequiredService<TenantResolver>().ShouldNotBeNull();
    }
}
