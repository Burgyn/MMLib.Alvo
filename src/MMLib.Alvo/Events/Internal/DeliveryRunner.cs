using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MMLib.Alvo.Events.Internal;

/// <summary>How one claimed delivery ended, for the dispatcher's tally.</summary>
internal enum DeliveryRunOutcome
{
    Succeeded,
    Failed,
    Dead,
    LeaseLost,
    Shutdown,
}

/// <summary>
/// Runs one claimed delivery: in its event's flow one hop deeper, in its own DI scope, as its handler's caller, under its
/// handler's timeout — then answers for the attempt, fenced, so a late finisher changes nothing (spec 2026-10-14 §4.6,
/// §4.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The handler is handed a session, never the data port.</b> <see cref="AlvoEventContext.Data"/> is
/// <see cref="IAlvoSessions.For"/> over the caller the runner chose — the system in the event's tenant, or the actor the
/// envelope recorded — so the handler cannot pick another identity through it.
/// </para>
/// <para>
/// <b>The timeout is enforced, not cooperative.</b> The handler starts on the thread pool, so even one that blocks its
/// thread cannot hold the runner past its timeout; the runner then cancels the handler's token, gives it
/// <see cref="SettleGrace"/> to stop. A handler that ends within that grace is answered by its own result — a success, or
/// a failure of its own — unless it ended by giving in to the cancellation, which is <c>timeout</c>. A handler still
/// running then is abandoned (.NET has no thread abort) and answered <c>timeout</c>; its late completion is never
/// recorded: the store's fence on (claimant, attempt) answers for each attempt once. Should such a handler fail later, its
/// exception's type is logged, never its message. While it runs it is tracked by <see cref="AbandonedHandlers"/>, which
/// stands its handler down at the cap.
/// </para>
/// <para>
/// <b>The answer is retried, inside the lease.</b> A storage refusal of the answer — a busy SQLite database, a PostgreSQL
/// serialization failure or deadlock — is written again after a short, doubling wait while the retry would still
/// <em>start</em> <see cref="AnswerMargin"/> before the handler's timeout + <see cref="LeaseGrace"/> +
/// <see cref="SettleGrace"/> since the attempt began — never later than the claim's lease — and no later than
/// <see cref="SettleGrace"/> after the first refusal, so a permanent refusal under a long timeout is not retried for an
/// hour. The write itself may then wait at SQLite's write gate and land after the lease; the fence decides — it records
/// the answer if nobody reclaimed the delivery, and changes nothing otherwise. A fence that no longer holds is never
/// retried. Without the retry, one refused write would cost an attempt whose side effect had already happened.
/// </para>
/// <para>
/// <b>The span answers for the attempt as recorded.</b> It ends after the store has answered, so a handler that finished
/// after its lease was taken over is <c>lease-lost</c>, not <c>succeeded</c>; the store calls run outside the handler's
/// flow, at the event's own depth.
/// </para>
/// <para>
/// <b>Fail closed on the envelope.</b> A payload that is no longer an Alvo envelope, or whose present <c>tenantid</c> —
/// or, for an actor run, <c>authid</c> — cannot be read, is dead <c>event-unreadable</c>: it never runs as a tenantless
/// system or an anonymous caller.
/// </para>
/// </remarks>
internal sealed class DeliveryRunner(
    IDeliveryStore store,
    IServiceScopeFactory scopes,
    IAlvoSessions sessions,
    IPolicyCatalogProvider catalogs,
    DeliveryBackoff backoff,
    AbandonedHandlers abandoned,
    TimeProvider time,
    ILogger<DeliveryRunner> logger)
{
    /// <summary>
    /// The margin a claim's lease adds to the longest handler timeout beside <see cref="SettleGrace"/>: the lease is the
    /// longest timeout + <see cref="LeaseGrace"/> + <see cref="SettleGrace"/>, so an attempt is answered — timed out and
    /// settled — with this much to spare for its scope and its payload read.
    /// </summary>
    internal static TimeSpan LeaseGrace { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long a timed-out handler is given to observe its token before it is abandoned — 30 s except in tests; a
    /// claim's lease must cover it on top of the timeout and <see cref="LeaseGrace"/>.
    /// </summary>
    internal TimeSpan SettleGrace { get; init; } = LeaseGrace;

    /// <summary>
    /// The room a retried answer leaves before the claim's lease ends: an answer is retried only while it would still start
    /// this long before the handler's timeout + <see cref="LeaseGrace"/> + <see cref="SettleGrace"/> since the attempt began.
    /// </summary>
    internal static TimeSpan AnswerMargin { get; } = TimeSpan.FromSeconds(5);

    private static TimeSpan FirstAnswerRetry { get; } = TimeSpan.FromMilliseconds(50);

    private static TimeSpan LongestAnswerRetry { get; } = TimeSpan.FromSeconds(1);

    private const int RetryLogEvery = 10;

    internal async Task<DeliveryRunOutcome> RunAsync(
        ClaimedDelivery delivery, BoundHandler handler, string claimant, CancellationToken stopping)
    {
        var startedAt = time.GetUtcNow();
        var started = time.GetTimestamp();
        var (@event, refusal) = Admit(delivery, handler);
        using var span = @event is null ? null : AlvoHookTelemetry.StartDelivery(delivery.Subscriber, @event, delivery.Attempts);
        var verdict = @event is null ? refusal : await AttemptAsync(@event, delivery, handler, stopping).ConfigureAwait(false);
        if (verdict is not { } answered)
        {
            return DeliveryRunOutcome.Shutdown;
        }

        var attempt = new DeliveryAttempt { Delivery = delivery, Claimant = claimant, StartedAt = startedAt, Duration = time.GetElapsedTime(started) };
        var answerBy = startedAt + handler.Settings.Timeout + LeaseGrace + SettleGrace - AnswerMargin;
        var outcome = await RecordAsync(attempt, handler.Settings.Retry, answered, new AnswerWindow(answerBy, Narrowed: false, stopping), span).ConfigureAwait(false);
        AlvoHookTelemetry.Finish(span, SpanOutcome(outcome));
        AlvoDeliveryMetrics.Recorded(attempt, outcome);

        return outcome;
    }

    /// <summary>
    /// Runs <paramref name="handler"/> once over <paramref name="event"/> exactly as an attempt would — its own scope, its
    /// identity, one hop deeper in the chain, its timeout, one delivery span — and records nothing; the handler's exception
    /// propagates as it was thrown — a <see cref="RecordUnreadableException"/> from reading a typed record included, which an
    /// attempt would record dead <c>record-unreadable</c>.
    /// </summary>
    /// <exception cref="TimeoutException">The handler ran past its timeout; it is cancelled and not waited for.</exception>
    internal async Task InvokeOnceAsync(AlvoEvent @event, BoundHandler handler, int attempt, CancellationToken cancellationToken)
    {
        var delivery = new ClaimedDelivery
        {
            Id = Guid.Empty,
            EventId = @event.Id,
            Subscriber = handler.Id,
            PartitionKey = @event.PartitionKey,
            Attempts = attempt,
        };
        using var flow = AlvoFlow.Enter(FlowOf(@event));
        using var span = AlvoHookTelemetry.StartDelivery(handler.Id, @event, attempt);
        try
        {
            await RunUnderTimeoutAsync(@event, delivery, handler, cancellationToken).ConfigureAwait(false);
            AlvoHookTelemetry.Finish(span, AlvoHookTelemetry.Succeeded);
        }
        catch (Exception)
        {
            AlvoHookTelemetry.Finish(span, AlvoHookTelemetry.Failed);
            throw;
        }
    }

    /// <summary>
    /// Runs the handler off the caller's thread, as <see cref="InvokeAsync"/> does, so one that blocks synchronously still
    /// times out; past its timeout it is cancelled and dropped — its later failure observed, never tracked as abandoned,
    /// since nothing records this run.
    /// </summary>
    private async Task RunUnderTimeoutAsync(AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var invocation = Task.Run(() => RunHandlerAsync(@event, delivery, handler, cancel.Token), CancellationToken.None);
        try
        {
            await invocation.WaitAsync(handler.Settings.Timeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            Observe(invocation);
            throw;
        }
    }

    private static void Observe(Task dropped) => _ = dropped.ContinueWith(
        ended => _ = ended.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    /// <summary>The verdict of a handler that has ended — its own result, whatever the runner's deadline did meanwhile.</summary>
    internal static AttemptVerdict Ended(Task invocation) =>
        invocation.IsCompletedSuccessfully ? AttemptVerdict.Success
        : invocation.Exception is { } failure ? VerdictOf(failure.InnerException ?? failure)
        : AttemptVerdict.Retry(typeof(TaskCanceledException).FullName!);

    private static (AlvoEvent? Event, AttemptVerdict? Refusal) Admit(ClaimedDelivery delivery, BoundHandler handler) =>
        delivery.Payload is null ? (null, AttemptVerdict.Dead(DeliveryErrors.EventMissing))
        : delivery.Attempts > handler.Settings.Retry.MaxAttempts ? (null, AttemptVerdict.Dead(DeliveryErrors.LeaseExpired))
        : ReadableEvent(delivery.Payload, handler.Settings.RunAs) is { } @event ? (@event, null)
        : (null, AttemptVerdict.Dead(DeliveryErrors.EventUnreadable));

    /// <summary>Runs the handler in its event's flow; the flow ends here, so the store calls that answer for it run outside it.</summary>
    private async Task<AttemptVerdict?> AttemptAsync(AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, CancellationToken stopping)
    {
        using var flow = AlvoFlow.Enter(FlowOf(@event));
        AlvoDeliveryMetrics.Lagged(delivery.Subscriber, time.GetUtcNow() - @event.Time);
        try
        {
            return await InvokeAsync(@event, delivery, handler, stopping).ConfigureAwait(false);
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string SpanOutcome(DeliveryRunOutcome outcome) => outcome switch
    {
        DeliveryRunOutcome.Succeeded => AlvoHookTelemetry.Succeeded,
        DeliveryRunOutcome.LeaseLost => AlvoHookTelemetry.LeaseLost,
        _ => AlvoHookTelemetry.Failed,
    };

    private async Task<AttemptVerdict> InvokeAsync(AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var invocation = Task.Run(() => RunHandlerAsync(@event, delivery, handler, cancel.Token), CancellationToken.None);
        try
        {
            await invocation.WaitAsync(handler.Settings.Timeout, time, stopping).ConfigureAwait(false);
            return AttemptVerdict.Success;
        }
        catch (TimeoutException) when (!stopping.IsCancellationRequested)
        {
            return invocation.IsCompleted
                ? Ended(invocation)
                : await TimedOutAsync(invocation, cancel, delivery, stopping).ConfigureAwait(false);
        }
        catch (Exception failure) when (!stopping.IsCancellationRequested)
        {
            LogUnreadable(failure, delivery);
            return VerdictOf(failure);
        }
    }

    /// <summary>Logs why a record could not be read as its host type: the message names the field, never a value.</summary>
    private void LogUnreadable(Exception failure, ClaimedDelivery delivery)
    {
        if (failure is RecordUnreadableException unreadable)
        {
            DeliveryLog.DeliveryRecordUnreadable(logger, delivery.Id, delivery.Subscriber, delivery.Attempts, unreadable.Message);
        }
    }

    /// <summary>Cancels a handler past its timeout, gives it <see cref="SettleGrace"/> to stop, and abandons it if it does not.</summary>
    private async Task<AttemptVerdict> TimedOutAsync(
        Task invocation, CancellationTokenSource cancel, ClaimedDelivery delivery, CancellationToken stopping)
    {
        await cancel.CancelAsync().ConfigureAwait(false);
        await SettleAsync(invocation, stopping).ConfigureAwait(false);
        if (invocation.IsCompleted)
        {
            return Settled(invocation);
        }

        Abandon(invocation, delivery);
        return AttemptVerdict.Retry(DeliveryErrors.Timeout);
    }

    /// <summary>
    /// The verdict of a handler that ended within its settle grace: its own result — a success, or a failure of its own —
    /// unless it ended by giving in to the cancellation, which is the runner's <c>timeout</c>.
    /// </summary>
    internal static AttemptVerdict Settled(Task invocation) =>
        invocation.IsCompletedSuccessfully ? AttemptVerdict.Success
        : invocation.Exception?.InnerException is { } failure && failure is not OperationCanceledException ? VerdictOf(failure)
        : AttemptVerdict.Retry(DeliveryErrors.Timeout);

    private async Task SettleAsync(Task invocation, CancellationToken stopping)
    {
        try
        {
            await invocation.WaitAsync(SettleGrace, time, stopping).ConfigureAwait(false);
        }
        catch (Exception) when (!stopping.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Counts an abandoned handler — and tracks it while it runs, so its handler stands down at the cap — and observes its
    /// later failure and logs its type, never its message, since nothing records it.
    /// </summary>
    private void Abandon(Task invocation, ClaimedDelivery delivery)
    {
        AlvoDeliveryMetrics.AbandonedRecorded(delivery.Subscriber);
        abandoned.Track(delivery.Subscriber, invocation);
        _ = invocation.ContinueWith(
            ended => DeliveryLog.AbandonedHandlerFailed(
                logger, delivery.Id, delivery.Subscriber, delivery.Attempts, TypeOf(ended.Exception!.InnerException ?? ended.Exception)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static AttemptVerdict VerdictOf(Exception failure) =>
        IsDepthRefusal(failure) ? AttemptVerdict.Dead(DeliveryErrors.ChainDepthExceeded)
        : failure is RecordUnreadableException ? AttemptVerdict.Dead(DeliveryErrors.RecordUnreadable)
        : AttemptVerdict.Retry(TypeOf(failure));

    private static string TypeOf(Exception failure) => failure.GetType().FullName ?? failure.GetType().Name;

    private static bool IsDepthRefusal(Exception failure) => failure switch
    {
        AlvoChainDepthExceededException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsDepthRefusal),
        { InnerException: { } inner } => IsDepthRefusal(inner),
        _ => false,
    };

    /// <summary>The stored envelope, or <see langword="null"/> when it cannot be read as one this handler may run on.</summary>
    internal static AlvoEvent? ReadableEvent(string payload, AlvoRunAs runAs)
    {
        try
        {
            var @event = AlvoEventJson.Read(payload);
            return Readable(@event, runAs) ? @event : null;
        }
        catch (Exception unreadable) when (unreadable is JsonException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A present but unreadable <c>tenantid</c> — or, for an actor run, <c>authid</c> — must never become a tenantless
    /// system caller or an anonymous one: the delivery is dead instead (fail closed).
    /// </summary>
    private static bool Readable(AlvoEvent @event, AlvoRunAs runAs) =>
        (@event.TenantId is null || EventActor.TenantOf(@event) is not null)
        && (runAs is not AlvoRunAs.EventActor || @event.AuthId is null || UserId.TryParse(@event.AuthId, CultureInfo.InvariantCulture, out _));

    private async Task RunHandlerAsync(AlvoEvent @event, ClaimedDelivery delivery, BoundHandler handler, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var context = new AlvoEventContext(@event, delivery.Subscriber, delivery.Attempts, sessions.For(CallerFor(@event, handler.Settings.RunAs)));
            var message = HandlerMessage.For(@event, handler.Registration);
            await handler.Registration.Invoker.InvokeAsync(scope.ServiceProvider, message, context, ct).ConfigureAwait(false);
        }
    }

    private AlvoContext CallerFor(AlvoEvent @event, AlvoRunAs runAs) =>
        runAs is AlvoRunAs.EventActor
            ? EventActor.Of(@event, catalogs.DeclaredRoles ?? RoleCatalog.BuiltInOnly)
            : AlvoContext.System(EventActor.TenantOf(@event));

    private static AlvoProvenance FlowOf(AlvoEvent @event) => new()
    {
        CorrelationId = @event.CorrelationId,
        CausationId = @event.Id.ToString(),
        Depth = @event.ChainDepth + 1,
    };

    /// <summary>Answers for the attempt; should the answer throw, the span still ends, as failed, before the failure goes on.</summary>
    private async Task<DeliveryRunOutcome> RecordAsync(
        DeliveryAttempt attempt, AlvoRetryPolicy policy, AttemptVerdict verdict, AnswerWindow window, Activity? span)
    {
        try
        {
            return await AnswerAsync(attempt, policy, verdict, window).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AlvoHookTelemetry.Finish(span, AlvoHookTelemetry.Failed);
            throw;
        }
    }

    private async Task<DeliveryRunOutcome> AnswerAsync(DeliveryAttempt attempt, AlvoRetryPolicy policy, AttemptVerdict verdict, AnswerWindow window)
    {
        if (verdict.Succeeded)
        {
            return await AnsweredAsync(() => store.CompleteAsync(attempt, CancellationToken.None), attempt, window).ConfigureAwait(false)
                ? DeliveryRunOutcome.Succeeded
                : LeaseLost(attempt);
        }

        var retryAfter = verdict.Retryable ? backoff.After(attempt.Delivery.Attempts, policy) : null;
        var failure = new DeliveryFailure { ErrorType = verdict.ErrorType!, RetryAfter = retryAfter, MaxAttempts = policy.MaxAttempts };

        return await AnsweredAsync(() => store.FailAsync(attempt, failure, CancellationToken.None), attempt, window).ConfigureAwait(false)
            ? Failed(attempt.Delivery, failure)
            : LeaseLost(attempt);
    }

    /// <summary>
    /// Writes the answer, and writes it again after a short, doubling wait when the storage refused it — a busy database, a
    /// serialization failure, a deadlock — while the retry would still start inside the lease and within
    /// <see cref="SettleGrace"/> of the first refusal. A fence that no longer holds is an answer (<see langword="false"/>),
    /// never retried; a stop ends the retries, and the lease returns the delivery. The first retry is logged, then every
    /// tenth.
    /// </summary>
    private async Task<bool> AnsweredAsync(Func<Task<bool>> answer, DeliveryAttempt attempt, AnswerWindow window)
    {
        var refusals = 0;
        for (var wait = FirstAnswerRetry; ; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestAnswerRetry.Ticks)))
        {
            try
            {
                return await answer().ConfigureAwait(false);
            }
            catch (Exception failure) when (IsStorageRefusal(failure))
            {
                window = window.AfterRefusal(time.GetUtcNow(), SettleGrace);
                if (!window.Admits(time.GetUtcNow() + wait))
                {
                    throw;
                }

                LogRetry(attempt, failure, ++refusals, wait);
            }

            await Task.Delay(wait, time, window.Stopping).ConfigureAwait(false);
        }
    }

    private void LogRetry(DeliveryAttempt attempt, Exception failure, int refusals, TimeSpan wait)
    {
        if (refusals == 1 || refusals % RetryLogEvery == 0)
        {
            DeliveryLog.DeliveryAnswerRetried(logger, attempt.Delivery.Id, attempt.Delivery.Subscriber, TypeOf(failure), refusals, wait);
        }
    }

    /// <summary>
    /// A storage refusal: a <see cref="DbException"/> anywhere in the chain. The core cannot tell an
    /// engine's transient codes apart — SQLite does not mark <c>SQLITE_BUSY</c> transient — and need not: the answer is a
    /// fenced, idempotent write, so retrying a permanent refusal costs only time inside the lease.
    /// </summary>
    private static bool IsStorageRefusal(Exception failure) => failure switch
    {
        DbException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsStorageRefusal),
        { InnerException: { } inner } => IsStorageRefusal(inner),
        _ => false,
    };

    private DeliveryRunOutcome Failed(ClaimedDelivery delivery, DeliveryFailure failure)
    {
        if (failure.RetryAfter is null || delivery.Attempts >= failure.MaxAttempts)
        {
            DeliveryLog.DeliveryDead(logger, delivery.Id, delivery.Subscriber, delivery.Attempts, failure.ErrorType);
            AlvoDeliveryMetrics.DeadRecorded(delivery.Subscriber, failure.ErrorType);
            return DeliveryRunOutcome.Dead;
        }

        DeliveryLog.DeliveryFailed(logger, delivery.Id, delivery.Subscriber, delivery.Attempts, failure.ErrorType);
        return DeliveryRunOutcome.Failed;
    }

    private DeliveryRunOutcome LeaseLost(DeliveryAttempt attempt)
    {
        DeliveryLog.DeliveryLeaseLost(logger, attempt.Delivery.Id, attempt.Delivery.Subscriber, attempt.Delivery.Attempts);
        return DeliveryRunOutcome.LeaseLost;
    }

    /// <summary>
    /// Until when an answer may still be retried — narrowed once, at the first refusal, to that refusal plus the settle
    /// grace — and the stop that ends the retries sooner.
    /// </summary>
    private readonly record struct AnswerWindow(DateTimeOffset AnswerBy, bool Narrowed, CancellationToken Stopping)
    {
        internal bool Admits(DateTimeOffset retryAt) => retryAt < AnswerBy && !Stopping.IsCancellationRequested;

        internal AnswerWindow AfterRefusal(DateTimeOffset now, TimeSpan grace) =>
            Narrowed ? this : this with { AnswerBy = AnswerBy < now + grace ? AnswerBy : now + grace, Narrowed = true };
    }

    internal readonly record struct AttemptVerdict(string? ErrorType, bool Retryable)
    {
        internal static AttemptVerdict Success => new(null, false);

        internal bool Succeeded => ErrorType is null;

        internal static AttemptVerdict Retry(string errorType) => new(errorType, true);

        internal static AttemptVerdict Dead(string errorType) => new(errorType, false);
    }
}
