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
/// <b>The loop observes its token, because the host blocks in <c>StopAsync</c> waiting for it</b> with a 30 s
/// <c>ShutdownTimeout</c>. Every await here takes the stopping token, and
/// the idle wait is a <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> rather than a sleep,
/// so a shutdown ends the pump in milliseconds instead of turning a clean stop into a half-minute hang. An
/// entry claimed when the shutdown arrives is left claimed: its lease is what recovers it, which is the one
/// mechanism that also covers a process that died.
/// </para>
/// <para>
/// <b>No transaction is opened here, and adding one would break the dispatcher on SQLite.</b> Claim, mark and
/// release are one autocommit statement each through <see cref="IOutboxStore"/>. Measured (spike Q5): a
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
/// </remarks>
/// <param name="store">The queue: claim under a lease, mark dispatched, release.</param>
/// <param name="boot">The readiness signal this service waits on before it claims anything.</param>
/// <param name="catalogs">The primed policy catalog every subscription is decided against.</param>
/// <param name="evaluator">The evaluator every hook condition is judged by.</param>
/// <param name="executor">Runs one matched hook's action; it decides nothing and catches nothing.</param>
/// <param name="options">The validated poll interval, batch size, attempt ceiling and lease.</param>
/// <param name="time">The clock the idle wait is measured on, so a test clock can drive the pump.</param>
/// <param name="logger">Where a failed attempt, an abandoned event and a stopped pump are recorded.</param>
internal sealed class OutboxDispatcher(
    IOutboxStore store,
    AlvoBootState boot,
    IPolicyCatalogProvider catalogs,
    IPredicateEvaluator evaluator,
    EventActionExecutor executor,
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

        if (await boot.SettledAsync(stoppingToken).ConfigureAwait(false) is not AlvoBootPhase.Ready)
        {
            return;
        }

        await store.EnsureAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await PumpOneBatchAsync(stoppingToken).ConfigureAwait(false) == 0)
            {
                await Task.Delay(options.Value.PollInterval, time, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Claims one batch and dispatches every entry in it.</summary>
    /// <param name="stoppingToken">The host's stopping token.</param>
    /// <returns>How many entries were claimed; <c>0</c> means the queue had nothing claimable.</returns>
    /// <remarks>
    /// <see langword="internal"/> so a test can drain the queue deterministically instead of sleeping past a
    /// poll interval and hoping. A drain that polls is a fact that passes when the pump is broken and the
    /// timeout is generous.
    /// </remarks>
    internal async Task<int> PumpOneBatchAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var claimed = await store
            .ClaimAsync(_claimant, settings.BatchSize, settings.MaxAttempts, settings.ClaimLease, stoppingToken)
            .ConfigureAwait(false);

        foreach (var entry in claimed)
        {
            await DispatchAsync(entry, stoppingToken).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    /// <summary>
    /// Delivers one entry, containing whatever it throws so neither the rest of the batch nor the host is
    /// affected by it.
    /// </summary>
    /// <remarks>
    /// The filter is what keeps a shutdown a shutdown: a cancelled token means the host is stopping, and
    /// swallowing that would leave the pump looping until the 30 s <c>ShutdownTimeout</c> expired.
    /// <c>WebhookDelivery</c> converts its own timeout into a <see cref="TimeoutException"/> for the same
    /// reason — a slow receiver must not read as a shutdown.
    /// </remarks>
    private async Task DispatchAsync(OutboxEntry entry, CancellationToken stoppingToken)
    {
        try
        {
            await DeliverAsync(entry, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!stoppingToken.IsCancellationRequested)
        {
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
    /// </remarks>
    private async Task DeliverAsync(OutboxEntry entry, CancellationToken stoppingToken)
    {
        var @event = AlvoEventJson.Read(entry.Payload);
        var hooks = EventSubscriptions.Matching(Catalog, @event, evaluator, logger);

        foreach (var hook in hooks)
        {
            await executor.ExecuteAsync(hook, @event, stoppingToken).ConfigureAwait(false);
        }

        await store.MarkDispatchedAsync(entry.Id, stoppingToken).ConfigureAwait(false);

        Counted(hooks.Count).Add(1);
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

    private static Counter<long> Counted(int matchedHooks) =>
        matchedHooks == 0 ? AlvoEventMetrics.Filtered : AlvoEventMetrics.Dispatched;

    /// <summary>
    /// The primed catalog, or a refusal — never an empty one.
    /// </summary>
    /// <remarks>
    /// The boot primes the catalog before it publishes <see cref="AlvoBootPhase.Ready"/>, so this cannot be null
    /// downstream of the gate and reaching it means the invariant broke. It throws rather than treating the
    /// events as unmatched, because "unmatched" retires them: the loud version costs a stopped pump and keeps
    /// every event, and the quiet version loses them all.
    /// </remarks>
    private PolicyCatalog Catalog =>
        catalogs.Current ?? throw new InvalidOperationException(
            "Alvo's outbox dispatcher reached a batch with no primed policy catalog. It waits for the boot to "
            + "publish Ready before it claims anything, and the boot primes the catalog before publishing, so "
            + "this is an invariant rather than a configuration mistake. Nothing was retired: an event judged "
            + "against an unprimed catalog would match no hook and be retired as delivered.");

    /// <summary>
    /// Who this process claims as, recorded on every entry it takes so an abandoned claim is attributable to a
    /// replica rather than to "something".
    /// </summary>
    private static readonly string _claimant = $"{Environment.MachineName}:{Environment.ProcessId}";
}
