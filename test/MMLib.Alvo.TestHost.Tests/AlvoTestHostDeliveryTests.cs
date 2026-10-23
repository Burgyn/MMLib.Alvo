using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Data;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>Delivery on the test's schedule, and never to the network — the package's own delivery promises.</summary>
public sealed class AlvoTestHostDeliveryTests
{
    private const string TicketsWebhook = "/entities/tickets/hooks/afterCreate/0";

    private static string HelpDesk => Path.Combine(AppContext.BaseDirectory, "descriptors", "help-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nothing_is_delivered_until_the_test_drains()
    {
        var receiver = new StubWebhookReceiver();
        await using var alvo = await StartAsync(receiver);
        await CreateHighPriorityAsync(alvo);

        await Task.Delay(TimeSpan.FromMilliseconds(1500), Ct);

        receiver.Deliveries.ShouldBeEmpty("the background loop would have delivered within one 1 s poll interval");
        await alvo.DrainEventsAsync(Ct);
        receiver.Deliveries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Without_a_receiver_a_webhook_fails_instead_of_reaching_the_network()
    {
        var logs = new RecordingLoggerProvider();
        await using var alvo = await AlvoTestHost.StartAsync(
            builder =>
            {
                builder.FromDescriptor(HelpDesk);
                builder.Services.AddLogging(logging => logging.AddProvider(logs));
            },
            new FakeTimeProvider(),
            webhooks: null,
            Ct);
        await CreateHighPriorityAsync(alvo);

        var drained = await alvo.DrainEventsAsync(Ct);

        drained.Claimed.ShouldBe(1);
        drained.Failed.ShouldBe(1, "the refusing default handler fails the attempt");
        drained.Subscribers.ShouldBe([TicketsWebhook]);
        logs.Exceptions.ShouldContain(
            failure => failure.ToString().Contains("without a webhook receiver", StringComparison.Ordinal),
            "the failed attempt says why, and how to pass a receiver");
    }

    [Fact]
    public async Task A_receiver_outlives_the_host_it_served()
    {
        var receiver = new StubWebhookReceiver();
        await using (var first = await StartAsync(receiver))
        {
            await CreateHighPriorityAsync(first);
            await first.DrainEventsAsync(Ct);
        }

        receiver.IsDisposed.ShouldBeFalse("the host owns the factory's handler, never the caller's");

        await using var second = await StartAsync(receiver);
        await CreateHighPriorityAsync(second);
        await second.DrainEventsAsync(Ct);

        receiver.Deliveries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_quiet_queue_drains_to_an_empty_result()
    {
        await using var alvo = await StartAsync(new StubWebhookReceiver());

        var drained = await alvo.DrainEventsAsync(Ct);

        drained.IsQuiet.ShouldBeTrue();
        drained.Subscribers.ShouldBeEmpty();
    }

    private static Task<AlvoTestHost> StartAsync(HttpMessageHandler? webhooks, TimeProvider? time = null) =>
        AlvoTestHost.StartAsync(alvo => alvo.FromDescriptor(HelpDesk), time, webhooks, Ct);

    private static Task<AlvoRecord> CreateHighPriorityAsync(AlvoTestHost alvo) => alvo.Data.CreateAsync(
        "tickets",
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "Server down", ["priority"] = "high" },
        AlvoTestHost.Caller("agent"),
        cancellationToken: Ct);
}
