using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Rules;

public class RulesSetupTests
{
    [Fact]
    public void AddAlvo_resolves_the_policy_engine()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IPolicyEngine>().ShouldNotBeNull();
    }
}
