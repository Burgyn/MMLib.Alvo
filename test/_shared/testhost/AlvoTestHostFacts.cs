using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// What an <see cref="AlvoTestHost"/> promises, on whichever engine the deriving class supplies: the SQLite leg in
/// <c>MMLib.Alvo.TestHost.Tests</c>, the PostgreSQL leg in <c>MMLib.Alvo.Data.PostgreSql.Tests.Integration</c>
/// (spec 2026-10-14 §7 HX-02, acceptance (4)). Linked into both, so neither engine is asked an easier question.
/// </summary>
public abstract class AlvoTestHostFacts
{
    protected const string HelpDesk = "help-desk.alvo.json";
    protected const string Tickets = "tickets";
    protected const string TicketsWebhook = "/entities/tickets/hooks/afterCreate/0";
    protected const string EchoLoop = "echo-loop.alvo.json";
    protected const string Echoes = "echoes";
    protected const string EchoesWebhook = "/entities/echoes/hooks/afterCreate/0";

    private static readonly DateTimeOffset _stamped = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static AlvoContext Agent { get; } = AlvoTestHost.Caller("agent");

    [Fact]
    public async Task A_descriptor_reject_refuses_through_the_data_port()
    {
        await using var alvo = await StartAsync(HelpDesk);
        var ticket = await alvo.Data.CreateAsync(Tickets, Values(("title", "Printer jam")), Agent, cancellationToken: Ct);
        var id = (Guid)ticket[AlvoManagedColumns.Id]!;

        var refusal = await Should.ThrowAsync<AlvoAuthorizationException>(
            () => alvo.Data.UpdateAsync(Tickets, id, Values(("status", "closed")), Agent, cancellationToken: Ct));

        refusal.Message.ShouldContain("Close a ticket with a resolution");
        (await alvo.Data.GetAsync(Tickets, id, Agent, Ct))!["status"].ShouldBe("open", "a refused write leaves no row change");
    }

    [Fact]
    public async Task The_host_clock_stamps_every_write()
    {
        await using var alvo = await StartAsync(HelpDesk, time: new FakeTimeProvider(_stamped));

        var ticket = await alvo.Data.CreateAsync(Tickets, Values(("title", "Printer jam")), Agent, cancellationToken: Ct);

        ticket[AlvoManagedColumns.CreatedAt].ShouldBe(_stamped);
    }

    [Fact]
    public async Task Draining_delivers_a_declarative_webhook_after_hook_to_the_receiver()
    {
        var receiver = new StubWebhookReceiver();
        await using var alvo = await StartAsync(HelpDesk, webhooks: receiver);
        await alvo.Data.CreateAsync(Tickets, Values(("title", "Server down"), ("priority", "high")), Agent, cancellationToken: Ct);
        await alvo.Data.CreateAsync(Tickets, Values(("title", "Mouse"), ("priority", "low")), Agent, cancellationToken: Ct);

        var drained = await alvo.DrainEventsAsync(Ct);

        receiver.Deliveries.ShouldHaveSingleItem().ShouldContain("Server down");
        drained.Claimed.ShouldBe(2, "both writes emitted an event; only the high-priority one matched the hook");
        drained.Failed.ShouldBe(0);
        drained.Subscribers.ShouldBe([TicketsWebhook]);
    }

    [Fact]
    public async Task A_receiver_that_writes_back_is_reported_as_a_loop()
    {
        var receiver = new StubWebhookReceiver();
        await using var alvo = await StartAsync(EchoLoop, webhooks: receiver);
        var ct = Ct;
        receiver.OnDelivery = () => alvo.Data.CreateAsync(Echoes, Values(("note", "again")), Agent, cancellationToken: ct);
        await alvo.Data.CreateAsync(Echoes, Values(("note", "first")), Agent, cancellationToken: Ct);

        var loop = await Should.ThrowAsync<AlvoDrainNotQuiescentException>(() => alvo.DrainEventsAsync(Ct));

        loop.Rounds.ShouldBe(100);
        loop.Subscribers.ShouldBe([EchoesWebhook]);
        loop.Message.ShouldContain(EchoesWebhook);
    }

    /// <summary>Registers this leg's database on the builder; the SQLite leg registers nothing and gets the default.</summary>
    protected abstract Task<Action<IAlvoBuilder>> DatabaseAsync();

    protected async Task<AlvoTestHost> StartAsync(
        string descriptor, TimeProvider? time = null, HttpMessageHandler? webhooks = null)
    {
        var database = await DatabaseAsync();

        return await AlvoTestHost.StartAsync(
            alvo =>
            {
                database(alvo);
                alvo.FromDescriptor(Path.Combine(AppContext.BaseDirectory, "descriptors", descriptor));
            },
            time,
            webhooks,
            Ct);
    }

    protected static Dictionary<string, object?> Values(params (string Field, object? Value)[] values) =>
        values.ToDictionary(value => value.Field, value => value.Value, StringComparer.Ordinal);
}
