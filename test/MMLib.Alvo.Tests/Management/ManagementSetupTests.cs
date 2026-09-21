using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The gate is registered by <c>AddAlvo</c> itself.
/// </summary>
/// <remarks>
/// A gate a container cannot build is a gate no route can attach, and the failure would surface as an
/// activation error on the first management request rather than at composition — so the registration is
/// held here rather than left to be noticed by whichever endpoint attaches the filter first.
/// </remarks>
public class ManagementSetupTests
{
    [Fact]
    public void AddAlvo_resolves_the_management_access_evaluator()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ManagementAccessEvaluator>().ShouldNotBeNull();
    }
}
