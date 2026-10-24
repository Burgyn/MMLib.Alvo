using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Expressions;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Rules;

using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The one thing that drains the outbox: claim a batch under a lease, deliver every after-hook each event is
/// subscribed to, retire what was delivered and hand back what was not.
/// </summary>
/// <remarks>
/// <para>
/// <b>It gates on <see cref="AlvoBootState"/>, and on .NET 10 that is the only way to express the gate.</b>
/// <see cref="BackgroundService.ExecuteAsync"/> now runs <em>entirely</em> off the startup thread — "no part of
/// it blocks other services from starting" — so "not before the schema is primed" cannot be expressed by
/// registration order, and <c>await Task.Yield()</c> as a first line is dead code. What the gate buys is not
/// tidiness: an unprimed <see cref="PolicyCatalog"/> knows no entity, so every event would match no hook, be
/// counted as filtered and be <b>retired</b> — silent, permanent event loss that no retry could recover,
/// because a filtered event is deliberately not retried. A refused boot stops the pump for the same reason.
/// </para>
/// <para>
/// <b>Nothing escapes <see cref="ExecuteAsync"/>.</b>
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> defaults to <c>StopHost</c>, and from .NET 11
/// <c>RunAsync</c>/<c>StopAsync</c> throw on a failed background service and the process exits non-zero — with
/// the documented recommended action being "do nothing", because a failing app should fail. One poison event
/// must not be that failure, so containment lives inside the loop, per entry, and the outermost catch is the
/// backstop rather than the mechanism.
/// </para>
/// <para>
/// <b>A failed round does not end the loop.</b> A claim, a release or a hand-back the store refuses — an outage, a
/// restarting database — fails the round, not the pump: it is logged (<see cref="EventLog.DispatcherRoundFailed"/>, the
/// exception's type name only) and the next round waits a backoff that doubles from the poll interval up to
/// <see cref="MaxRoundBackoff"/>, reset by a round that succeeds. Nothing it claimed was retired, so a claim it
/// could not answer for returns when its lease ends.
/// </para>
/// <para>
/// <b>The loop observes its token, because the host blocks in <c>StopAsync</c> waiting for it</b> with a 30 s
/// <c>ShutdownTimeout</c>. Every await here takes the stopping token, and
/// the idle wait is a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> rather than a sleep,
/// so a shutdown ends the pump in milliseconds instead of turning a clean stop into a half-minute hang. An
/// entry claimed when the shutdown arrives is left claimed: its lease is what recovers it, which is the one
/// mechanism that also covers a process that died.
/// </para>
/// <para>
/// <b>No transaction is opened here, and adding one would break the dispatcher on SQLite.</b> Claim, mark and
/// release are one statement each through <see cref="IOutboxStore"/>: mark and release on autocommit, and the claim
/// in a write-first transaction of its own inside the store (on SQLite <c>BEGIN IMMEDIATE</c>), because an
/// autocommit <c>UPDATE … RETURNING</c> commits inside the row read, where a busy lock is not retried. Measured (spike Q5): a
/// transaction that reads and then writes is the single shape that fails unretryably with
/// <c>SQLITE_BUSY_SNAPSHOT</c> after burning the whole 30-second retry loop under WAL, and fails the
/// <em>request path</em> instead under the shipped journal mode. Wrapping two store calls in a transaction to be
/// tidy is the edit that would undo it.
/// </para>
/// <para>
/// <b>There is no dispatcher-wide caller, and that is a correction rather than a simplification.</b> A hook
/// condition's <c>@user.id</c> is resolved per event from the envelope's own <c>authid</c> by
/// <see cref="EventSubscriptions"/>, because the actor an author means is the one who made the change and not
/// whoever happens to be draining the queue. A shared <see cref="AlvoContext.System"/> answered both
/// <c>@user.id</c> and <c>@user.roles</c> with the framework's own identity — so
/// <c>new.owner_id != @user.id</c> never matched and <c>'admin' in @user.roles</c> always did — and answered
/// <c>@tenant.id</c> with <see langword="null"/>, which the interpreter's null rule turns into "every tenant"
/// under a negation. The two references an envelope cannot answer are now refused when the hook is compiled.
/// </para>
/// <para>
/// <b>It requires <see cref="IOutboxStore"/> to resolve, which widens what a database provider owes.</b> Every
/// relational provider already registers one (<c>AddRelationalProvider</c>), so no shipped provider is affected
/// today; a provider that is not built on that registration — the dynamic-entity driver F7 brings, or any
/// non-EF adapter — must supply the port from the moment this dispatcher is registered. Stated the way
/// deviation 60 states <c>IRuntimeSchemaWriter</c>'s widening, so a later provider author reads it as an
/// obligation rather than discovering it as a DI failure at startup.
/// </para>
/// <para>
/// <b>The same tick runs by hand in the manual dispatch mode.</b> In <see cref="AlvoEventDispatchMode.Manual"/> the loop
/// logs that it stood down and returns; <see cref="EventPump"/> then calls <see cref="ManualRoundAsync"/>, which keeps
/// the loop's own gates (the mode, and a Ready boot with a primed catalog) and runs one <see cref="PumpOneBatchAsync"/>
/// tick.
/// </para>
/// </remarks>
/// <param name="store">The queue: claim under a lease, mark dispatched, release.</param>
/// <param name="boot">The readiness signal this service waits on before it claims anything.</param>
/// <param name="catalogs">The primed policy catalog every subscription is decided against.</param>
/// <param name="evaluator">The evaluator every hook condition is judged by.</param>
/// <param name="executor">Runs one matched hook's action; it decides nothing and catches nothing.</param>
/// <param name="fanOut">Records the deliveries each claimed event fans out to, before its actions run.</param>
/// <param name="options">The validated poll interval, batch size, attempt ceiling and lease.</param>
/// <param name="time">The clock the idle wait is measured on, so a test clock can drive the pump.</param>
/// <param name="logger">Where a failed attempt, an abandoned event and a stopped pump are recorded.</param>
internal sealed class OutboxDispatcher(
    IOutboxStore store,
    AlvoBootState boot,
    IPolicyCatalogProvider catalogs,
    IPredicateEvaluator evaluator,
    EventActionExecutor executor,
    DeliveryFanOut fanOut,
    IOptions<AlvoEventOptions> options,
    TimeProvider time,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    /// <inheritdoc/>
    /// <remarks>
    /// The cancellation arm is empty on purpose: a cancelled <paramref name="stoppingToken"/> is the host
    /// stopping, which is the pump finishing its job rather than failing at it. Everything else is contained and
    /// logged, because an escaping failure would stop a host that is serving HTTP.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PumpUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            EventLog.DispatcherStopped(logger, failure);
        }
    }

    /// <summary>Waits for the boot, then claims batch after batch until the host stops.</summary>
    private async Task PumpUntilStoppedAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        if (options.Value.DispatchMode is AlvoEventDispatchMode.Manual)
        {
            EventLog.DispatcherManual(logger);
            return;
        }

        if (await boot.SettledAsync(stoppingToken).ConfigureAwait(false) is not AlvoBootPhase.Ready)
        {
            return;
        }

        // READY IS NO LONGER SUFFICIENT, and the reason is worth the extra line. Since #83 a
        // dashboard-first host publishes Ready with NOTHING primed when its project has no descriptor
        // history yet (SchemaStartupOutcome.Awaiting) — the expected first state of `docker run`. Pumping
        // there is not merely useless, it is destructive: `Catalog` below throws, the per-entry containment
        // in DeliverAsync catches it and calls AbandonAttemptAsync, and `attempts` climbs to MaxAttempts
        // within about a minute at the shipped defaults — after which ClaimAsync excludes those entries
        // FOREVER and this build has no DLQ to recover them from. An embedded host that published an
        // application event before its first descriptor, or an operator who typo'd the project name against
        // a populated database, would lose real deliveries.
        if (catalogs.Current is null)
        {
            EventLog.DispatcherStoodDownUnprimed(logger);

            return;
        }

        var failing = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            failing = await TickAsync(failing, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One round of the loop and the wait after it: none after a full batch, the poll interval after an empty one, and
    /// a growing backoff after a failed one.
    /// </summary>
    /// <param name="failing">The backoff the previous round left; <see cref="TimeSpan.Zero"/> when it succeeded.</param>
    /// <param name="stoppingToken">The host's stopping token.</param>
    /// <returns>The backoff the next round starts from.</returns>
    private async Task<TimeSpan> TickAsync(TimeSpan failing, CancellationToken stoppingToken)
    {
        TimeSpan wait;
        try
        {
            await EnsureStoreAsync(stoppingToken).ConfigureAwait(false);
            var claimed = await PumpOneBatchAsync(stoppingToken).ConfigureAwait(false);
            failing = TimeSpan.Zero;
            wait = claimed == 0 ? options.Value.PollInterval : TimeSpan.Zero;
        }
        catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
        {
            failing = wait = NextBackoff(failing, options.Value.PollInterval);
            EventLog.DispatcherRoundFailed(logger, failure.GetType().FullName ?? failure.GetType().Name, wait);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, stoppingToken).ConfigureAwait(false);
        }

        return failing;
    }

    /// <summary>The wait after a failed round: the poll interval first, then double the last, up to <see cref="MaxRoundBackoff"/>.</summary>
    /// <param name="previous">The wait after the previous failed round; <see cref="TimeSpan.Zero"/> when it succeeded.</param>
    /// <param name="pollInterval">The configured poll interval — also the ceiling when it is longer than a minute.</param>
    internal static TimeSpan NextBackoff(TimeSpan previous, TimeSpan pollInterval)
    {
        if (previous <= TimeSpan.Zero)
        {
            return pollInterval;
        }

        var ceiling = pollInterval > MaxRoundBackoff ? pollInterval : MaxRoundBackoff;

        return previous >= ceiling / 2 ? ceiling : previous * 2;
    }

    /// <summary>The longest a failed round makes the loop wait, unless the poll interval itself is longer.</summary>
    internal static TimeSpan MaxRoundBackoff { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Claims one batch and dispatches every entry in it.</summary>
    /// <param name="stoppingToken">The host's stopping token.</param>
    /// <returns>How many entries were claimed; <c>0</c> means the queue had nothing claimable.</returns>
    /// <remarks>
    /// <see langword="internal"/> so a test can drain the queue deterministically instead of sleeping past a
    /// poll interval and hoping. A drain that polls is a fact that passes when the pump is broken and the
    /// timeout is generous.
    /// </remarks>
    internal async Task<int> PumpOneBatchAsync(CancellationToken stoppingToken) =>
        (await RoundAsync(stoppingToken).ConfigureAwait(false)).Claimed;

    /// <summary>One tick on the caller's schedule, behind the same gates the loop keeps.</summary>
    /// <exception cref="InvalidOperationException">Not in the manual mode, events disabled, or the boot not Ready.</exception>
    internal async Task<AlvoDrainResult> ManualRoundAsync(CancellationToken cancellationToken)
    {
        EnsureManual();
        await EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        return await RoundAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One tick: claim what is due, dispatch every entry, and say what happened.</summary>
    private async Task<AlvoDrainResult> RoundAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var claimed = await store
            .ClaimAsync(_claimant, settings.BatchSize, settings.MaxAttempts, settings.ClaimLease, stoppingToken)
            .ConfigureAwait(false);
        var round = new DrainRound();
        var stopped = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        foreach (var entry in claimed)
        {
            await DispatchAsync(entry, round, stopped, stoppingToken).ConfigureAwait(false);
        }

        return round.Result(claimed.Count);
    }

    /// <summary>Refuses a manual round unless this process is configured to be driven by hand, with events on.</summary>
    private void EnsureManual()
    {
        if (options.Value.DispatchMode is not AlvoEventDispatchMode.Manual)
        {
            throw NotManual(options.Value);
        }

        if (!options.Value.Enabled)
        {
            throw EventsDisabled();
        }
    }

    /// <summary>
    /// Refuses a round before the boot has primed a catalog — the same gate the loop keeps — and brings the store up
    /// once. Callers are serialised by <see cref="EventPump"/>, so the flag needs no lock.
    /// </summary>
    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (boot.Phase is not AlvoBootPhase.Ready)
        {
            throw NotReady();
        }

        if (catalogs.Current is null)
        {
            throw Unprimed();
        }

        await EnsureStoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the store up once per process; a failure is retried by the next round. The loop and the manual rounds never
    /// run in one process together, so the flag needs no lock.
    /// </summary>
    private async Task EnsureStoreAsync(CancellationToken cancellationToken)
    {
        if (!_storeReady)
        {
            await store.EnsureAsync(cancellationToken).ConfigureAwait(false);
            _storeReady = true;
        }
    }

    private static InvalidOperationException NotManual(AlvoEventOptions configured) => new(
        "A manual dispatch round was refused before anything was claimed: this process drains its outbox with the "
        + "background loop, and two drivers of one outbox break per-entity-key ordering. Set "
        + $"{AlvoEventOptionsConfiguration.DispatchModeKey} to '{AlvoEventDispatchMode.Manual}' and "
        + $"{AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.Enabled)} to true to drive it with "
        + $"IAlvoEventPump.DrainAsync. Configured: mode '{configured.DispatchMode}', enabled {configured.Enabled}.");

    private static InvalidOperationException EventsDisabled() => new(
        "A manual dispatch round was refused before anything was claimed: the dispatch mode is manual, but events are "
        + $"switched off ({AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.Enabled)} = false), so there is "
        + $"nothing to drive. Set {AlvoEventOptions.SectionName}:{nameof(AlvoEventOptions.Enabled)} to true to drive "
        + "the outbox with IAlvoEventPump.DrainAsync.");

    private static InvalidOperationException NotReady() => new(
        "A manual dispatch round was refused before anything was claimed: the boot has not reached Ready, and an event "
        + "judged against an unprimed catalog would match no hook and be retired as delivered. Start the host and let "
        + "its boot finish first.");

    private static InvalidOperationException Unprimed() => new(
        "A manual dispatch round was refused before anything was claimed: the boot is Ready but primed no schema, "
        + "because the project has no descriptor applied yet, and an event judged against an unprimed catalog would "
        + "match no hook and be retired as delivered. Apply a descriptor to the project first; if it should already "
        + $"have one, check {AlvoSchemaOptions.ProjectEnvironmentVariable}.");

    /// <summary>
    /// Delivers one entry, containing whatever it throws so neither the rest of the batch nor the host is
    /// affected by it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The filter is what keeps a shutdown a shutdown: a cancelled token means the host is stopping, and
    /// swallowing that would leave the pump looping until the 30 s <c>ShutdownTimeout</c> expired.
    /// <c>WebhookDelivery</c> converts its own timeout into a <see cref="TimeoutException"/> for the same
    /// reason — a slow receiver must not read as a shutdown.
    /// </para>
    /// <para>
    /// An entry whose partition key a failed fan-out stopped earlier in this batch is held back untried
    /// (<see cref="IOutboxStore.HoldBackAsync"/>): handed back for as long as the failed one waits, without spending an
    /// attempt, so no later event of a record is fanned out before an earlier one.
    /// </para>
    /// </remarks>
    private async Task DispatchAsync(
        OutboxEntry entry, DrainRound round, Dictionary<string, TimeSpan> stopped, CancellationToken stoppingToken)
    {
        if (stopped.TryGetValue(entry.PartitionKey, out var heldFor))
        {
            await HoldBackAsync(entry, heldFor, stoppingToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await DeliverAsync(entry, round, stopped, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
        {
            round.Failed();
            await AbandonAttemptAsync(entry, failure, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs every hook the entry's event is subscribed to, retires the entry, and counts it exactly once.
    /// </summary>
    /// <remarks>
    /// The entry is retired <b>after</b> the last action and the counter increments <b>after</b> the retirement,
    /// so "dispatched" means what <c>alvo.events.dispatched</c> claims it means: every matched hook ran and the
    /// row is gone from the queue. An event that matched nothing takes the same path and increments
    /// <c>alvo.events.filtered</c> instead — once per event, never per hook — and writes no execution-log entry
    /// at all, which is the half of that criterion no counter can express.
    /// <para>
    /// <b>There is no per-hook progress, so a partial failure re-runs the hooks that already succeeded.</b> Two
    /// hooks match, the first POSTs, the second throws: nothing is retired, the whole entry is released, and the
    /// next attempt runs the first hook again. That is a consequence of at-least-once rather than an oversight —
    /// the receiver is already told to deduplicate on the event's <c>id</c>
    /// (<see cref="MMLib.Alvo.Events.IEmailSender"/> and <see cref="EventActionExecutor"/> both say so), and that
    /// key is unchanged by a redelivery whichever hook triggers it. Recording per-hook progress would mean a
    /// second piece of mutable state per entry to keep consistent with the row, which buys nothing a dedup key
    /// does not already buy.
    /// </para>
    /// <para>
    /// <b>Fan-out runs first.</b> A failing action re-runs the actions on the next attempt; the deliveries already
    /// recorded are not touched, and a second fan-out of the event records nothing. An event that produced a delivery —
    /// even a dead one — counts as dispatched, never filtered.
    /// </para>
    /// </remarks>
    private async Task DeliverAsync(
        OutboxEntry entry, DrainRound round, Dictionary<string, TimeSpan> stopped, CancellationToken stoppingToken)
    {
        var @event = AlvoEventJson.Read(entry.Payload);
        var catalog = Catalog;
        var deliveries = await FanOutAsync(entry, @event, catalog, stopped, stoppingToken).ConfigureAwait(false);
        var hooks = EventSubscriptions.Matching(catalog, @event, evaluator, logger);

        foreach (var hook in hooks)
        {
            round.Ran(hook.Path);
            await RunAsync(hook, @event, stoppingToken).ConfigureAwait(false);
        }

        await store.MarkDispatchedAsync(entry.Id, stoppingToken).ConfigureAwait(false);

        Counted(hooks.Count, deliveries).Add(1);
    }

    /// <summary>
    /// Hands a held-back entry back untried; when the store refuses, the entry stays claimed and undispatched — so the
    /// lane condition still keeps its record's later deliveries behind it — and its claim lease returns it.
    /// </summary>
    private async Task HoldBackAsync(OutboxEntry entry, TimeSpan heldFor, CancellationToken stoppingToken)
    {
        try
        {
            await store.HoldBackAsync(entry.Id, heldFor, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
        {
            EventLog.HoldBackFailed(logger, entry.Id, failure.GetType().FullName ?? failure.GetType().Name);
        }
    }

    /// <summary>
    /// Fans the event out; a storage failure holds back every later entry of the batch with the same partition key, for
    /// as long as this one waits, so no later event of the record can be fanned out first (spec 2026-10-14 §4.8).
    /// </summary>
    private async Task<int> FanOutAsync(
        OutboxEntry entry,
        AlvoEvent @event,
        PolicyCatalog catalog,
        Dictionary<string, TimeSpan> stopped,
        CancellationToken stoppingToken)
    {
        try
        {
            return await fanOut.FanOutAsync(@event, catalog, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            stopped[entry.PartitionKey] = RetryAfter(entry);
            throw;
        }
    }

    /// <summary>Runs one hook's action under its <c>alvo.delivery</c> span, recording how it ended.</summary>
    /// <remarks>
    /// The failure is rethrown untouched: the span observes it, the entry's release still decides it. A cancellation
    /// while the host stops is neither outcome, exactly as <see cref="DispatchAsync"/> does not count it as an attempt
    /// (a cancellation landing between the two filters reads as failed here only, which is harmless).
    /// </remarks>
    private async Task RunAsync(CompiledAfterHook hook, AlvoEvent @event, CancellationToken stoppingToken)
    {
        using var delivery = AlvoHookTelemetry.StartDelivery(hook.Path, @event);
        try
        {
            await executor.ExecuteAsync(hook, @event, stoppingToken).ConfigureAwait(false);
            AlvoHookTelemetry.Finish(delivery, AlvoHookTelemetry.Succeeded);
        }
        catch when (!stoppingToken.IsCancellationRequested)
        {
            AlvoHookTelemetry.Finish(delivery, AlvoHookTelemetry.Failed);
            throw;
        }
    }

    /// <summary>
    /// Hands the entry back for a later claim, <b>after a backoff</b>, and says so loudly once it has reached
    /// the ceiling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attempt count is not rolled back by <see cref="IOutboxStore.ReleaseAsync"/>, which is what makes the
    /// ceiling reachable at all. Past it the entry is simply never claimed again — not deleted, not moved — so
    /// "abandoned" stays observable: the row keeps <c>dispatched_at IS NULL</c>, <c>alvo.events.failed</c> has
    /// one increment per attempt, and <see cref="EventLog.PoisonEvent"/> is the Error line naming it. That is
    /// this build's whole stand-in for a dead-letter queue (7.1 owns the real one).
    /// </para>
    /// <para>
    /// <b>The backoff is what makes the ceiling a bound on <em>time</em> and not only on count.</b> The idle
    /// wait in <see cref="PumpUntilStoppedAsync"/> runs only when a claim came back <em>empty</em>, so without a
    /// per-entry backoff a released entry is claimable on the very next iteration and a receiver that is
    /// restarting burns all <see cref="AlvoEventOptions.MaxAttempts"/> attempts in milliseconds — permanently
    /// abandoning an event this build has no queue to recover it from, and hitting the receiver with the whole
    /// batch at line rate on the way. That directly defeats <c>WebhookDelivery</c>'s own justification for not
    /// classifying failures: it declines to distinguish a permanently wrong endpoint from one thirty seconds
    /// from finishing, so it has to survive the thirty seconds.
    /// </para>
    /// </remarks>
    private async Task AbandonAttemptAsync(OutboxEntry entry, Exception failure, CancellationToken stoppingToken)
    {
        AlvoEventMetrics.Failed.Add(1);
        EventLog.ActionFailed(logger, entry.Id, entry.Type, entry.Attempts, failure);

        await store.ReleaseAsync(entry.Id, RetryAfter(entry), stoppingToken).ConfigureAwait(false);

        if (entry.Attempts >= options.Value.MaxAttempts)
        {
            EventLog.PoisonEvent(logger, entry.Id, entry.Type, entry.Attempts);
        }
    }

    /// <summary>How long this entry waits before it is claimable again: one poll interval per attempt so far.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="AlvoEventOptions.PollInterval"/> is the unit rather than a knob of its own, because it already
    /// means "how long the pump waits before looking again" — the queue's own tick. Multiplying by the attempt
    /// count makes the wait grow linearly, which is the cheap half of every published webhook retry schedule
    /// (Standard Webhooks' reference schedule steps 5 s → 5 m → 30 m), and it bounds the ceiling in time: at the
    /// shipped defaults ten attempts span at least 1+2+…+9 = 45 seconds, comfortably past a receiver's restart.
    /// </para>
    /// <para>
    /// It is not exponential, and that is deliberate: nothing here classifies a failure, so a large multiplier
    /// would push a genuinely transient 503 out by hours for the same reason it would push out a permanently
    /// wrong endpoint. Real per-status scheduling belongs with the dead-letter queue that can absorb it (7.1).
    /// </para>
    /// </remarks>
    private TimeSpan RetryAfter(OutboxEntry entry) => entry.Attempts * options.Value.PollInterval;

    /// <summary>
    /// The counter one retired event increments: <c>alvo.events.dispatched</c> when it matched a hook or fanned out to any
    /// delivery — a dead one included — and <c>alvo.events.filtered</c> only when it reached nothing at all.
    /// </summary>
    internal static Counter<long> Counted(int matchedHooks, int deliveries) =>
        matchedHooks == 0 && deliveries == 0 ? AlvoEventMetrics.Filtered : AlvoEventMetrics.Dispatched;

    /// <summary>
    /// The primed catalog, or a refusal — never an empty one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ready alone no longer implies a primed catalog</b>, so <see cref="PumpUntilStoppedAsync"/> checks
    /// for one explicitly before it claims anything: since #83 a dashboard-first host publishes Ready with
    /// nothing primed when its project has no descriptor history yet. Downstream of *that* gate this still
    /// cannot be null, and reaching it still means an invariant broke.
    /// </para>
    /// <para>
    /// It throws rather than treating the events as unmatched, because "unmatched" retires them. Note what
    /// the throw actually costs, since the earlier wording promised something this type does not do: the
    /// per-entry containment in <c>DeliverAsync</c> catches it and abandons the attempt, so it is the
    /// <em>entry's</em> attempt counter that pays, not the pump. That is survivable for a genuine invariant
    /// break on one entry and was catastrophic for the unprimed state, which is why the unprimed state is
    /// now refused at the gate instead of here.
    /// </para>
    /// </remarks>
    private PolicyCatalog Catalog =>
        catalogs.Current ?? throw new InvalidOperationException(
            "Alvo's outbox dispatcher reached a batch with no primed policy catalog. It waits for the boot to "
            + "publish Ready before it claims anything, and the boot primes the catalog before publishing, so "
            + "this is an invariant rather than a configuration mistake. Nothing was retired: an event judged "
            + "against an unprimed catalog would match no hook and be retired as delivered.");

    private bool _storeReady;

    /// <summary>
    /// Who this process claims as, recorded on every entry it takes so an abandoned claim is attributable to a
    /// replica rather than to "something".
    /// </summary>
    private static readonly string _claimant = $"{Environment.MachineName}:{Environment.ProcessId}";
}
