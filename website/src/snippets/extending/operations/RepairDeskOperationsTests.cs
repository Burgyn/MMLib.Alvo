using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost;
using Shouldly;
using Xunit;

namespace RepairDesk.Tests;

public sealed class RepairDeskOperationsTests
{
    private const string Project = "repair-desk";

    private static string Descriptor => Path.Combine(AppContext.BaseDirectory, "repair-desk.alvo.json");

    [Fact]
    public async Task A_dead_delivery_is_reproduced_from_its_fixture_and_redelivered_once_fixed()
    {
        var ct = TestContext.Current.CancellationToken;
        var supplier = new Supplier { Down = true };
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));
        await using var alvo = await StartAsync(supplier, clock, ct);
        await alvo.Data.CreateAsync("repairs", new Dictionary<string, object?> { ["bike"] = "BK-7", ["part"] = "brake pads" }, AlvoTestHost.Caller("mechanic"), cancellationToken: ct);
        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(36) })
        {
            clock.Advance(wait);
            await alvo.DrainEventsAsync(ct);
        }

        var operations = AsLead(alvo);
        var dead = (await operations.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { Status = "dead" }, ct)).Items.ShouldHaveSingleItem();
        dead.LastErrorType.ShouldBe("System.Net.Http.HttpRequestException");

        var fixture = await operations.GetFixtureAsync(Project, dead.Id, ct);
        fixture.Envelope.GetProperty("data").GetProperty("record").GetProperty("part").GetString().ShouldBe("<redacted:part>");
        await Should.ThrowAsync<HttpRequestException>(() => alvo.DeliverAsync(fixture, ct));

        supplier.Down = false;
        await operations.RedeliverAsync(Project, dead.Id, "supplier API is back", ct);
        await alvo.DrainEventsAsync(ct);
        supplier.Ordered.ShouldBe(["brake pads"]);
    }

    [Fact]
    public async Task A_paused_handler_catches_up_in_order_once_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var supplier = new Supplier();
        await using var alvo = await StartAsync(supplier, new FakeTimeProvider(), ct);
        var operations = AsLead(alvo);

        await operations.SetPausedAsync(Project, "order-parts", paused: true, "supplier maintenance window", ct);
        foreach (var part in new[] { "chain", "brake pads" })
        {
            await alvo.Data.CreateAsync("repairs", new Dictionary<string, object?> { ["bike"] = "BK-7", ["part"] = part }, AlvoTestHost.Caller("mechanic"), cancellationToken: ct);
        }

        await alvo.DrainEventsAsync(ct);
        supplier.Ordered.ShouldBeEmpty();

        await operations.SetPausedAsync(Project, "order-parts", paused: false, reason: null, ct);
        await alvo.DrainEventsAsync(ct);
        supplier.Ordered.ShouldBe(["chain", "brake pads"]);
    }

    private static Task<AlvoTestHost> StartAsync(Supplier supplier, TimeProvider clock, CancellationToken ct) =>
        AlvoTestHost.StartAsync(builder =>
        {
            builder.FromDescriptor(Descriptor).AddRepairDesk();
            builder.Services.AddSingleton<IPartsSupplier>(supplier);
        }, clock, cancellationToken: ct);

    private static IAlvoOperations AsLead(AlvoTestHost alvo)
    {
        alvo.Services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = AlvoTestHost.Caller("lead"),
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "docs",
        };
        return alvo.Services.GetRequiredService<IAlvoOperations>();
    }

    private sealed class Supplier : IPartsSupplier
    {
        private readonly List<string> _ordered = [];

        public bool Down { get; set; }

        public IReadOnlyList<string> Ordered => _ordered;

        public Task OrderAsync(string part, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("The parts supplier did not answer.");
            }

            _ordered.Add(part);
            return Task.CompletedTask;
        }
    }
}
