using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Reads.Internal;

namespace MMLib.Alvo.Tests.Reads;

/// <summary>
/// The read runner runs inside every read, possibly inside a unit's transaction: like the write-hook runner, nothing its
/// constructor takes may reach the network or another data port.
/// </summary>
public sealed class ReadExtensionRunnerArchitectureTests
{
    [Fact]
    public void Nothing_the_read_runner_s_constructor_takes_reaches_the_network_or_a_data_port()
    {
        var parameters = typeof(ReadExtensionRunner).GetConstructors().ShouldHaveSingleItem().GetParameters()
            .Select(parameter => parameter.ParameterType).ToList();

        parameters.ShouldNotBeEmpty();
        parameters.Where(HookDependencies.IsBanned).ShouldBeEmpty();
        parameters.ShouldNotContain(typeof(IServiceProvider), "a root provider would let the runner resolve anything");
    }
}
