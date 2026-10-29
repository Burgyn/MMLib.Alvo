using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Management;
using MMLib.Alvo.TestHost.Internal;
using System.Diagnostics;

namespace MMLib.Alvo.TestHost;

/// <summary>
/// A real Alvo backend inside a test: the production write pipeline, rules, hooks and outbox over a private database,
/// started from the same <c>AddAlvo</c> builder a host uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing above the database is faked.</b> <see cref="Data"/> behaves exactly as the host's own <c>IAlvoData</c>:
/// a descriptor <c>reject</c> refuses, a <c>mutate</c> applies and a rule hides rows as in production. Write through
/// <see cref="Data"/> rather than through <see cref="Services"/>: from write hooks on, it is what scopes a write's hooks
/// to <see cref="Hooks"/>. Unless the builder registers a provider of its own — <c>UsePostgreSql</c>, for parity runs —
/// each host gets a fresh SQLite database in memory, private to it and dropped when the host is disposed.
/// </para>
/// <para>
/// <b>Events wait for the test.</b> The dispatcher runs in the manual dispatch mode, so nothing is delivered until the
/// test drains. Webhooks go to the handler passed as <c>webhooks</c>; without one, every webhook attempt fails without
/// touching the network. <see cref="Hooks"/> records which hooks ran inside this host's drains.
/// </para>
/// </remarks>
public sealed class AlvoTestHost : IAsyncDisposable
{
    private const int MaxRounds = 100;
    private const string DrainActivityName = "alvo.test.drain";
    private const string TestEventType = "test.delivery";

    private static readonly ActivitySource _drainSource = new(AlvoHookRecorder.DrainSourceName);

    private readonly TestHostComposition _composition;
    private readonly IAlvoEventPump _pump;
    private readonly IAlvoData _data;
    private readonly AlvoHookRecorder _hooks = new();
    private bool _disposed;

    private AlvoTestHost(TestHostComposition composition)
    {
        _composition = composition;
        _pump = composition.Host.Services.GetRequiredService<IAlvoEventPump>();
        _data = composition.Host.Services.GetRequiredService<IAlvoData>();
    }

    /// <summary>Gets the data port, behaving exactly as the host's own endpoints and services reach it.</summary>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public IAlvoData Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _data;
        }
    }

    /// <summary>Gets the host's services, for any other Alvo port a test needs, such as <c>IAlvoEvents</c>.</summary>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public IServiceProvider Services
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _composition.Host.Services;
        }
    }

    /// <summary>Gets the record of which hooks and subscribers ran in this host's drains.</summary>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public AlvoHookRecorder Hooks
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _hooks;
        }
    }

    /// <summary>Starts a backend and waits until its descriptor is applied.</summary>
    /// <param name="configure">
    /// Configures Alvo as <c>AddAlvo</c> would — at least <c>FromDescriptor</c>. Registering a provider here (for
    /// example <c>UsePostgreSql</c>) replaces the in-memory SQLite database.
    /// </param>
    /// <param name="time">
    /// The clock every timestamp and retry backoff reads; the system clock when omitted. A <c>FakeTimeProvider</c> pins
    /// it.
    /// </param>
    /// <param name="webhooks">
    /// Receives every webhook delivery in place of the network. The host never disposes it, so one handler can serve
    /// several hosts. Without one, every webhook attempt fails.
    /// </param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>
    /// The started host; dispose it to stop the host and, unless the builder registered a provider of its own, drop its
    /// in-memory database.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="Migrations.AlvoStartupRefusedException">The boot refused the descriptor or its database.</exception>
    /// <exception cref="InvalidOperationException">The host started, but its boot did not reach <c>Ready</c>.</exception>
    /// <exception cref="AggregateException">
    /// The start failed and tearing the half-started host down failed too; the start's own failure comes first.
    /// </exception>
    public static async Task<AlvoTestHost> StartAsync(
        Action<IAlvoBuilder> configure,
        TimeProvider? time = null,
        HttpMessageHandler? webhooks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var composition = TestHostComposition.Build(configure, time, webhooks);
        try
        {
            await composition.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await composition.DisposeAfterAsync(failure).ConfigureAwait(false);
            throw;
        }

        return new AlvoTestHost(composition);
    }

    /// <summary>
    /// A caller holding <c>authenticated</c> plus <paramref name="roles"/>, as a new user each time.
    /// </summary>
    /// <param name="roles">
    /// The application roles the caller holds, as the descriptor's rules name them. A name the descriptor never declares
    /// is accepted as given and simply fails every rule that asks for a declared role.
    /// </param>
    /// <returns>The caller context to pass to <see cref="Data"/>.</returns>
    public static AlvoContext Caller(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        return new AlvoContext
        {
            User = new UserId(Guid.NewGuid()),
            Roles = RoleCatalog.Create(roles).Resolve([Role.Authenticated.Name, .. roles]),
        };
    }

    /// <summary>
    /// An event context for calling a class handler's <c>HandleAsync</c> yourself: data access as <paramref name="caller"/>
    /// through this host's real pipeline, and a synthetic event of type <c>test.delivery</c> with a fresh id.
    /// </summary>
    /// <remarks>
    /// Not a delivery: writes through the context's <c>Data</c> carry no causation from the synthetic event and sit no hop
    /// deeper in the chain. Write through the host and let a handler run on its delivery to test causation or depth.
    /// </remarks>
    /// <param name="caller">The caller the handler acts as; <see cref="Caller"/> builds one.</param>
    /// <param name="subscriber">The handler id the context reports.</param>
    /// <param name="attempt">The attempt the context reports, from 1.</param>
    /// <returns>The context to pass to the handler.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="caller"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subscriber"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public AlvoEventContext EventContext(AlvoContext caller, string subscriber = "test-handler", int attempt = 1)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var services = Services;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();

        return AlvoEventContext.Create(TestEvent(now), services.GetRequiredService<IAlvoSessions>().For(caller), subscriber, attempt);
    }

    /// <summary>The synthetic envelope <see cref="EventContext"/> hands the handler, minted at <paramref name="now"/>.</summary>
    private static AlvoEvent TestEvent(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();

        return new AlvoEvent
        {
            Id = AlvoEventId.Create(utc),
            Source = AlvoEvent.DefaultSource,
            Type = TestEventType,
            Time = utc,
            Subject = TestEventType,
            PartitionKey = TestEventType,
            AuthType = AlvoEventAuthType.Unknown,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Data = new AlvoEventData(),
        };
    }

    /// <summary>
    /// Delivers what is queued, round after round, until a round finds nothing due — including the events the
    /// deliveries themselves cause.
    /// </summary>
    /// <remarks>
    /// Each round is one tick of the production dispatchers: the same hooks, the same retry backoff on the host's clock.
    /// Under a <c>FakeTimeProvider</c> a failed attempt waits out its backoff, so the drain returns with it counted in
    /// <see cref="AlvoDrainResult.Failed"/>; advance the clock and drain again to retry it. Under the system clock a drain
    /// that runs longer than the backoff retries it again — pass a fake clock whenever a delivery can fail. The limit
    /// counts rounds, not cycles: a backlog larger than 100 rounds of <c>Alvo:Events:BatchSize</c> entries is refused
    /// the same way, though nothing loops.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the drain between or inside rounds.</param>
    /// <returns>
    /// Every non-quiet round summed: entries claimed, entries whose delivery failed, and the subscribers attempted.
    /// </returns>
    /// <exception cref="AlvoDrainNotQuiescentException">The queue was still busy after 100 rounds.</exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async Task<AlvoDrainResult> DrainEventsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var drain = StartDrain();
        var progress = new DrainProgress();
        for (var round = 0; round < MaxRounds; round++)
        {
            var result = await _pump.DrainAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsQuiet)
            {
                return progress.Total();
            }

            progress.Add(result);
        }

        throw AlvoDrainNotQuiescentException.After(MaxRounds, progress.Recent());
    }

    /// <summary>
    /// Runs the handler a fixture names, once, over its envelope — as a delivery attempt on this host would — and records
    /// nothing; the handler's exception reaches you as it was thrown. Take the fixture from <c>IAlvoOperations.GetFixtureAsync</c>
    /// (or the Management API's <c>…/deliveries/{id}/fixture</c>) on the instance where the delivery failed.
    /// </summary>
    /// <remarks>
    /// The fixture's non-key values arrive as <c>&lt;redacted:FIELD&gt;</c>; fill in what the reproduction needs before calling
    /// this. A typed handler reads the record as its host type first, so a redacted value in a typed field it cannot hold — a
    /// number, a date — fails with an <see cref="InvalidOperationException"/> naming the field before the handler runs. The run
    /// counts for <see cref="Hooks"/> like a delivered one.
    /// </remarks>
    /// <param name="fixture">The fixture.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when the handler returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fixture"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The handler is not registered here, the envelope cannot be read, or a typed field holds a value its type cannot.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The host was disposed.</exception>
    public async Task DeliverAsync(DeliveryFixture fixture, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(fixture);

        using var drain = StartDrain();
        await _pump.DeliverAsync(fixture, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the drain's root activity on a fresh trace and hands its id to <see cref="Hooks"/>, so the runs inside it —
    /// and only those — are this host's.
    /// </summary>
    private Activity? StartDrain()
    {
        var root = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        _hooks.Track(root.TraceId);
        var drain = _drainSource.StartActivity(DrainActivityName, ActivityKind.Internal, root);

        return drain;
    }

    /// <summary>
    /// Stops the host and, unless the builder registered a provider of its own, drops its in-memory database. Safe to
    /// call more than once.
    /// </summary>
    /// <returns>A task that completes when the host has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hooks.Stop();
        await _composition.DisposeAsync().ConfigureAwait(false);
    }
}
