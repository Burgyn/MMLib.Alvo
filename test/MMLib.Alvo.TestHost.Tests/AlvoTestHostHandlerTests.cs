using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Handler facts that only the package's own leg needs — engine-agnostic by construction.</summary>
public sealed class AlvoTestHostHandlerTests
{
    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_handler_that_drains_is_refused_instead_of_hanging()
    {
        Exception? refused = null;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(HelpDesk).Entity("tickets", tickets => tickets.OnCreated(
                "drains-from-inside",
                async (AlvoCreated<AlvoRecord> _, IAlvoEventPump pump, CancellationToken ct) =>
                {
                    refused = await Record.ExceptionAsync(() => pump.DrainAsync(ct));
                })),
            cancellationToken: Ct);
        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);

        await alvo.DrainEventsAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        refused.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("inside a round");
        alvo.Hooks.AssertRan("drains-from-inside", times: 1);
    }
}
