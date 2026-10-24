using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace TicketDesk.Typed.Tests;

public sealed class TicketDeskTypedHandlersTests
{
    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "ticket-desk.alvo.json");

    [Fact]
    public async Task Resolving_a_ticket_posts_to_the_chat_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var chat = new RecordingChat();
        await using var alvo = await AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddTicketDeskTypedHandlers();
            builder.Services.AddSingleton<IChatNotifier>(chat);
        }, cancellationToken: ct);
        var agent = AlvoTestHost.Caller("agent");

        var ticket = await alvo.Data.CreateAsync(
            "tickets", new Dictionary<string, object?> { ["title"] = "Printer jam", ["priority"] = "low", ["status"] = "open" }, agent, cancellationToken: ct);
        await alvo.Data.UpdateAsync(
            "tickets", (Guid)ticket["id"]!, new Dictionary<string, object?> { ["status"] = "resolved" }, agent, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        chat.Posted.ShouldBe(["Ticket 'Printer jam' resolved"]);
        alvo.Hooks.AssertRan("ticket-resolved-to-chat", times: 1);
        alvo.Hooks.AssertRan("open-follow-up", times: 0);

        await alvo.Data.CreateAsync(
            "tickets", new Dictionary<string, object?> { ["title"] = "Server down", ["priority"] = "high", ["status"] = "open" }, agent, cancellationToken: ct);
        await alvo.DrainEventsAsync(ct);

        alvo.Hooks.AssertRan("open-follow-up", times: 1);
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "follow_ups" }, agent, ct)).Items.ShouldHaveSingleItem();
    }

    private sealed class RecordingChat : IChatNotifier
    {
        private readonly List<string> _posted = [];

        public IReadOnlyList<string> Posted => _posted;

        public Task PostAsync(string message, CancellationToken cancellationToken)
        {
            _posted.Add(message);
            return Task.CompletedTask;
        }
    }
}
