using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Events;

using Xunit;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// One started backend the event acceptance criteria run over: the real data port writing to a real engine, the
/// real dispatcher draining the real outbox, and three probes recording what came out of it.
/// </summary>
/// <remarks>
/// <para>
/// Linked into both driver test projects rather than copied, for the reason <see cref="RecordingMeterListener"/>
/// is: both criteria are engine-agnostic by construction, and two per-engine copies of the drain and the tally
/// are two chances for the engines to stop being asked the same question. Everything engine-specific arrives as
/// the started database.
/// </para>
/// <para>
/// <b>Nothing here is a substitute for a production collaborator except the socket.</b> The write path is
/// <see cref="IAlvoData"/>, the queue is the real <c>alvo_outbox</c> table, the subscription decision and the
/// action are the dispatcher's own, and the only thing stood in for is the network underneath the named
/// <c>HttpClient</c>. That is what makes a count here a count of what would ship.
/// </para>
/// <para>
/// <b>The drain is the dispatcher's own <c>PumpOneBatchAsync</c>, bounded, and it throws on giving up.</b> A
/// drain that slept and hoped would make every assertion after it an under-count, and a drain that gave up
/// quietly would turn a stuck queue into a passing absence.
/// </para>
/// <para>
/// The tally is <see cref="OutboxTallyProbe"/>'s, shared with the chaos criterion rather than restated here: two
/// copies of "what is pending and what is retired" is how one criterion's anti-vacuity check comes to disagree
/// with the other's.
/// </para>
/// </remarks>
internal sealed class AlvoEventCriteriaWorld : IAlvoEventWorld
{
    /// <summary>
    /// Stands the world up: builds the probes, hands their registrations to
    /// <paramref name="startDatabase"/>, and resolves the dispatcher out of the container it built.
    /// </summary>
    /// <param name="project">The entity, schema, descriptor, caller and meter the criteria are measured on.</param>
    /// <param name="startDatabase">
    /// Starts one engine's database and container, applying the registrations it is handed before the provider
    /// is built. The per-engine fixture is the only thing that differs between the two legs.
    /// </param>
    internal static async Task<AlvoEventCriteriaWorld> StartAsync(
        AlvoEventProject project,
        Func<Action<IServiceCollection>, Task<(IAlvoData Data, IServiceProvider Services)>> startDatabase)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(startDatabase);

        var receiver = new RecordingWebhookReceiver();
        var log = new RecordingEventLog();
        var meter = new RecordingMeterListener(project.MeterName);

        var (data, services) = await startDatabase(container => Install(container, receiver, log));

        return new AlvoEventCriteriaWorld(project, data, services, receiver, log, meter);
    }

    /// <inheritdoc/>
    public IReadOnlyList<AlvoEventDelivery> Deliveries => _receiver.Deliveries;

    /// <inheritdoc/>
    public IReadOnlyList<AlvoEventLogEntry> ActionLogEntries => _log.ActionLogEntries;

    /// <inheritdoc/>
    public IAlvoEventMeter Metrics => _meter;

    /// <inheritdoc/>
    public async Task<Guid> CreateAsync(string status)
    {
        var created = await _data.CreateAsync(
            _project.Entity, Values(status, plate: null), _project.Caller, cancellationToken: Ct);

        return (Guid)created[AlvoManagedColumns.Id]!;
    }

    /// <inheritdoc/>
    public Task UpdateAsync(Guid id, string? status = null, string? plate = null) => _data.UpdateAsync(
        _project.Entity, id, Values(status, plate), _project.Caller, cancellationToken: Ct);

    /// <inheritdoc/>
    public async Task DrainAsync()
    {
        for (var claim = 0; claim < MaxClaims; claim++)
        {
            if (await _dispatcher.PumpOneBatchAsync(Ct) == 0)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            $"The outbox still had claimable entries after {MaxClaims} claims, with "
            + $"{(await TallyAsync()).Pending} undelivered. Every count taken after a drain would be an "
            + "under-count, so this fails loudly instead of letting a stuck queue read as an absence.");
    }

    /// <inheritdoc/>
    public Task<AlvoOutboxTally> TallyAsync() => _tally.TallyAsync();

    /// <summary>
    /// Disposes the meter listener, and nothing else: the container and the database belong to the fixture that
    /// started them.
    /// </summary>
    /// <remarks>
    /// The event counters are process-wide statics, so a listener that outlived its world would keep summing
    /// the next one's measurements into this one's totals.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        _meter.Dispose();

        return ValueTask.CompletedTask;
    }

    private AlvoEventCriteriaWorld(
        AlvoEventProject project,
        IAlvoData data,
        IServiceProvider services,
        RecordingWebhookReceiver receiver,
        RecordingEventLog log,
        RecordingMeterListener meter)
    {
        _project = project;
        _data = data;
        _receiver = receiver;
        _log = log;
        _meter = meter;
        _tally = new OutboxTallyProbe(services);
        _dispatcher = services.GetServices<IHostedService>().OfType<OutboxDispatcher>().Single();
    }

    /// <summary>
    /// The two registrations a criteria run needs, applied to the container the fixture is about to build.
    /// </summary>
    /// <remarks>
    /// The minimum level is <see cref="LogLevel.Trace"/> on purpose: the default pipeline drops nothing above
    /// Information, but a level filter that silently dropped the execution-log entry would make "no
    /// execution-log entry" true for the wrong reason.
    /// </remarks>
    private static void Install(
        IServiceCollection services, RecordingWebhookReceiver receiver, RecordingEventLog log)
    {
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        services.AddHttpClient(WebhookDelivery.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => receiver);
    }

    private static Dictionary<string, object?> Values(string? status, string? plate)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (status is not null)
        {
            values["status"] = status;
        }

        if (plate is not null)
        {
            values["plate"] = plate;
        }

        return values;
    }

    /// <summary>
    /// How many claims a drain may take before it gives up. Generous against the largest criterion here (102
    /// events over a batch size of 100) and still bounded, so a queue whose entries keep being released fails
    /// in seconds instead of looping forever.
    /// </summary>
    private const int MaxClaims = 64;

    private readonly AlvoEventProject _project;
    private readonly IAlvoData _data;
    private readonly RecordingWebhookReceiver _receiver;
    private readonly RecordingEventLog _log;
    private readonly RecordingMeterListener _meter;
    private readonly OutboxDispatcher _dispatcher;
    private readonly OutboxTallyProbe _tally;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
