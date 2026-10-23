using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace HelpDesk.Tests;

public sealed class HelpDeskTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "help-desk.alvo.json");

    [Fact]
    public async Task A_ticket_cannot_be_closed_without_a_resolution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(b => b.FromDescriptor(Descriptor), cancellationToken: ct);
        var agent = AlvoTestHost.Caller("agent");

        var ticket = await alvo.Data.CreateAsync("tickets", Values(("title", "Printer jam")), agent, cancellationToken: ct);

        await Should.ThrowAsync<AlvoAuthorizationException>(() => alvo.Data.UpdateAsync(
            "tickets", (Guid)ticket["id"]!, Values(("status", "closed")), agent, cancellationToken: ct));
    }

    [Fact]
    public async Task A_high_priority_ticket_notifies_the_ops_board()
    {
        var ct = TestContext.Current.CancellationToken;
        var opsBoard = new OpsBoard();
        await using var alvo = await AlvoTestHost.StartAsync(
            b => b.FromDescriptor(Descriptor), webhooks: opsBoard, cancellationToken: ct);

        await alvo.Data.CreateAsync(
            "tickets", Values(("title", "Server down"), ("priority", "high")), AlvoTestHost.Caller("agent"), cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        opsBoard.Received.ShouldBe(1);
        alvo.Hooks.AssertRan("/entities/tickets/hooks/afterCreate/0", times: 1);
    }

    private static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value);
}
