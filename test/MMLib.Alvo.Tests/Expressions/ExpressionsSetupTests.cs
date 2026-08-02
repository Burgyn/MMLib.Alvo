using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Tests.Expressions;

public class ExpressionsSetupTests
{
    [Fact]
    public void AddAlvo_resolves_the_cel_compiler()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICelCompiler>().ShouldNotBeNull();
    }
}
