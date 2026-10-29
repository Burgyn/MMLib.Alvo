using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Management;

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.TestHost.Tests;

/// <summary>
/// A delivery taken from one host as a fixture reproduces its failure in another (spec 2026-10-14 HX-06 acceptance (6)), and
/// the stats a host reports are its own (acceptance (8)).
/// </summary>
public sealed class AlvoTestHostDeliverTests
{
    private const string Handler = "send-confirmation";

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "descriptors", "ops-desk.alvo.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fixture_from_one_host_reproduces_the_failure_in_another()
    {
        DeliveryFixture fixture;
        await using (var production = await StartAsync(Failing))
        {
            await production.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam", ["status"] = "open" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
            await production.DrainEventsAsync(Ct);
            var operations = AsOperator(production);
            var dead = (await operations.ListDeliveriesAsync("ops-desk", new ManagementDeliveryQuery { Status = "dead" }, Ct)).Items.ShouldHaveSingleItem();
            fixture = await operations.GetFixtureAsync("ops-desk", dead.Id, Ct);
        }

        await using var local = await StartAsync(Failing);

        (await Should.ThrowAsync<InvalidOperationException>(() => local.DeliverAsync(fixture, Ct))).Message.ShouldBe("template missing");
        fixture.Envelope.GetRawText().ShouldNotContain("Printer jam");
    }

    [Fact]
    public async Task A_delivered_fixture_runs_the_handler_as_the_dispatcher_would_and_is_counted_as_a_run()
    {
        var seen = new List<(Guid Event, int Attempt, string Subscriber, bool System)>();
        await using var alvo = await StartAsync((AlvoCreated<AlvoRecord> _, AlvoEventContext context) =>
            seen.Add((context.EventId, context.Attempt, context.Subscriber, context.Caller.ActorKind == AlvoActorKind.System)));
        var envelope = System.Text.Json.JsonDocument.Parse(AlvoEventJson.Write(Event())).RootElement;

        await alvo.DeliverAsync(new DeliveryFixture { Subscriber = Handler, Delivery = Guid.NewGuid(), Attempt = 4, Envelope = envelope }, Ct);

        seen.ShouldHaveSingleItem().ShouldBe((envelope.GetProperty("id").GetGuid(), 4, Handler, true), "as a delivery: its attempt, its id, its identity");
        alvo.Hooks.AssertRan(Handler, times: 1);
    }

    /// <summary>
    /// A typed handler cannot read a redacted number: the delivery fails naming the field, and runs once the field is filled
    /// in — the fixture's redaction is a starting point, not a reproduction by itself.
    /// </summary>
    [Fact]
    public async Task A_typed_handler_names_the_redacted_field_it_cannot_read_until_it_is_filled_in()
    {
        DeliveryFixture fixture;
        await using (var production = await StartAsync(Failing))
        {
            await production.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "Printer jam", ["estimate"] = 3L }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
            await production.DrainEventsAsync(Ct);
            var operations = AsOperator(production);
            var dead = (await operations.ListDeliveriesAsync("ops-desk", new ManagementDeliveryQuery { Status = "dead" }, Ct)).Items.ShouldHaveSingleItem();
            fixture = await operations.GetFixtureAsync("ops-desk", dead.Id, Ct);
        }

        var estimates = new List<long?>();
        await using var local = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Descriptor).Entity<OpsTicket>(t => t.OnCreated(
                Handler, (AlvoCreated<OpsTicket> created) => estimates.Add(created.New.Estimate), configure: options => options.Retry = AlvoRetryPolicy.None)),
            cancellationToken: Ct);

        var unreadable = await Record.ExceptionAsync(() => local.DeliverAsync(fixture, Ct));
        var envelope = JsonNode.Parse(fixture.Envelope.GetRawText())!;
        envelope["data"]!["record"]!["estimate"] = 3;
        await local.DeliverAsync(fixture with { Envelope = JsonSerializer.SerializeToElement(envelope) }, Ct);

        unreadable.ShouldNotBeNull().Message.ShouldContain("estimate");
        estimates.ShouldBe([3L]);
        (await AsOperator(local).ListDeliveriesAsync("ops-desk", new ManagementDeliveryQuery(), Ct)).Items
            .ShouldBeEmpty("neither the refused run nor the one that ran recorded a delivery");
    }

    [Fact]
    public async Task Stats_count_the_runs_this_instance_made()
    {
        var id = $"counted-{Guid.NewGuid():N}";
        await using var alvo = await AlvoTestHost.StartAsync(
            builder => builder.FromDescriptor(Descriptor).Entity("tickets", t => t.OnCreated(id, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask)),
            cancellationToken: Ct);
        await alvo.Data.CreateAsync("tickets", new Dictionary<string, object?> { ["title"] = "a" }, AlvoTestHost.Caller("agent"), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        var stats = await AsOperator(alvo).GetExtensionStatsAsync("ops-desk", Ct);

        stats.Scope.ShouldBe("this-instance");
        var item = stats.Items.ShouldHaveSingleItem();
        item.Id.ShouldBe(id);
        item.Invocations.ShouldBe(1);
        item.P95.ShouldNotBeNull();
    }

    private static Func<AlvoCreated<AlvoRecord>, Task> Failing { get; } = _ => throw new InvalidOperationException("template missing");

    private static Task<AlvoTestHost> StartAsync(Delegate handler) => AlvoTestHost.StartAsync(
        builder => builder.FromDescriptor(Descriptor).Entity("tickets", t => t.OnCreated(Handler, handler, configure: options => options.Retry = AlvoRetryPolicy.None)),
        time: new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)),
        cancellationToken: Ct);

    private static IAlvoOperations AsOperator(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("ops"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    /// <summary>The part of a ticket the typed handler maps.</summary>
    [AlvoEntity("tickets")]
    public sealed class OpsTicket
    {
        public string? Title { get; set; }

        public long? Estimate { get; set; }
    }

    private static AlvoEvent Event() => new()
    {
        Id = AlvoEventId.Create(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero)),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
        Subject = "tickets/1",
        PartitionKey = "tickets:1",
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["title"] = "x" }) },
    };
}
