using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Calling a class handler yourself (spec 2026-10-14 HX-03 settled details (53)): the context reads and writes through the real pipeline.</summary>
public sealed class AlvoTestHostEventContextTests
{
    [Fact]
    public async Task A_class_handler_called_directly_writes_as_the_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", "handler-desk.alvo.json")), cancellationToken: ct);
        var agent = AlvoTestHost.Caller("agent");
        var context = alvo.EventContext(agent);

        await new CopyTitle().HandleAsync(new AlvoCreated<AlvoRecord> { New = new AlvoRecord(new Dictionary<string, object?> { ["title"] = "copied" }) }, context, ct);

        (context.Caller, context.Attempt, context.Subscriber, context.Event.Type).ShouldBe((agent, 1, "test-handler", "test.delivery"));
        (await alvo.Data.QueryAsync(new AlvoQuery { Entity = "tickets" }, agent, ct)).Items.ShouldHaveSingleItem()["title"].ShouldBe("copied");
    }

    private sealed class CopyTitle : IAlvoEventHandler<AlvoCreated<AlvoRecord>>
    {
        public Task HandleAsync(AlvoCreated<AlvoRecord> message, AlvoEventContext context, CancellationToken cancellationToken) =>
            context.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = message.New["title"] }, cancellationToken);
    }
}
