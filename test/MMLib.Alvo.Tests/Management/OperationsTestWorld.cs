using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.TestHost.Tests;
using MMLib.Alvo.Testing.Migrations;
using MMLib.Alvo.Tests.Events;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// A host registered the way a deployment registers one, over the in-memory delivery store, with one handler on
/// <c>tickets</c>, the project <c>desk</c> booted, and — unless told otherwise — the bootstrap administrator published.
/// </summary>
internal sealed class OperationsTestWorld : IDisposable
{
    internal const string Project = "desk";
    internal const string Handler = "send-confirmation";

    private readonly ServiceProvider _provider;

    internal OperationsTestWorld(bool admitted = true)
    {
        Store = new FakeDeliveryStore(Clock);
        var services = new ServiceCollection();
        services.AddSingleton(ManagementCallers.Bootstrapped(user => user == ManagementCallers.Bootstrap));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddLogging(logging => logging.AddProvider(Audit));
        services.AddAlvo(alvo =>
        {
            alvo.Services.AddSingleton<ISchemaMigrator>(new InMemorySchemaMigrator());
            alvo.Services.AddSingleton<IDeliveryStore>(Store);
            alvo.Entity("tickets", tickets => tickets.OnCreated(Handler, (AlvoCreated<AlvoRecord> _) => Task.CompletedTask));
        });
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<AlvoBootState>().Ready(Project, appliedRevision: 1);
        if (admitted)
        {
            Publish();
        }
    }

    internal ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

    internal FakeDeliveryStore Store { get; }

    internal AuditCapture Audit { get; } = new();

    internal AlvoOperationsService Service => _provider.GetRequiredService<AlvoOperationsService>();

    internal IServiceProvider Services => _provider;

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fans one event of <paramref name="key"/> out to <paramref name="subscriber"/>, dead when a reason is given, and returns the delivery.</summary>
    internal async Task<StoredDelivery> DeliveryAsync(string key, string? deadReason = null, string subscriber = Handler, bool keepEnvelope = true)
    {
        var @event = Event(key);
        if (keepEnvelope)
        {
            Store.Publish(@event);
        }

        await Store.FanOutAsync([new DeliveryRequest { EventId = @event.Id, Subscriber = subscriber, PartitionKey = key, Kind = "handler", DeadReason = deadReason }], Ct);

        return (await Store.ListAsync(new DeliveryFilter { Limit = 1 }, Ct))[0];
    }

    public void Dispose() => _provider.Dispose();

    private void Publish() =>
        _provider.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext { User = ManagementCallers.Bootstrap, Roles = new HashSet<Role> { Role.Authenticated } },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = "ops-key",
        };

    private AlvoEvent Event(string key) => new()
    {
        Id = AlvoEventId.Create(Clock.GetUtcNow()),
        Source = AlvoEvent.DefaultSource,
        Type = "entity.tickets.created",
        Time = Clock.GetUtcNow(),
        Subject = key.Replace(':', '/'),
        PartitionKey = key,
        AuthType = AlvoEventAuthType.ApiKey,
        CorrelationId = "4bf92f3577b34da6a3ce929d0e0e4736",
        Data = new AlvoEventData { Record = new AlvoRecord(new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["title"] = "Printer jam" }) },
    };
}
