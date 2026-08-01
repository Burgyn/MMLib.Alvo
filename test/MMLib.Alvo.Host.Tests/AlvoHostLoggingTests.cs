namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// Deviation 34's cost, made observable. <c>AddAlvo()</c> calls <c>AddLogging()</c> so the core can write its
/// declared-but-unhonoured-subsystems warning, and the deviation states plainly that "with no logging
/// <em>provider</em> configured the warning is dropped silently". A standalone host configures providers, so
/// this is the first place the warning can be shown to actually arrive.
/// </summary>
public class AlvoHostLoggingTests
{
    [Fact]
    public async Task The_unhonoured_subsystem_warning_reaches_the_hosts_logging_provider()
    {
        var descriptor = AlvoHostWorld.DescriptorPath("host-boot-with-webhooks.alvo.json");

        await using var world = await AlvoHostWorld.StartAsync(descriptor, overrides: null);

        world.Logs.Records.ShouldContain(
            record => record.StartsWith("Warning: ", StringComparison.Ordinal)
                && record.Contains("webhooks", StringComparison.Ordinal),
            "an operator who declares a subsystem Alvo does not honour must be told, and a dropped warning "
            + "is indistinguishable from an honoured subsystem");
    }
}
