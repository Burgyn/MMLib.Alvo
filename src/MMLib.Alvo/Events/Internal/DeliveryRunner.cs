using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
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
/// serialization failure or deadlock — is written again after a short, doubling wait while the retry would still start
/// <see cref="AnswerMargin"/> before the handler's timeout + <see cref="LeaseGrace"/> + <see cref="SettleGrace"/> —
/// never longer than the claim's lease, which covers the longest timeout. A fence that no longer holds is never retried.
/// Without it, one refused write would cost an attempt whose side effect had already happened.
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
        var outcome = await RecordAsync(attempt, handler.Settings.Retry, answered, new AnswerWindow(answerBy, stopping), span).ConfigureAwait(false);
        AlvoHookTelemetry.Finish(span, SpanOutcome(outcome));
        AlvoDeliveryMetrics.Recorded(attempt, outcome);

        return outcome;
    }

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
            return VerdictOf(failure);
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
        IsDepthRefusal(failure) ? AttemptVerdict.Dead(DeliveryErrors.ChainDepthExceeded) : AttemptVerdict.Retry(TypeOf(failure));

    private static string TypeOf(Exception failure) => failure.GetType().FullName ?? failure.GetType().Name;

    private static bool IsDepthRefusal(Exception failure) => failure switch
    {
        AlvoChainDepthExceededException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsDepthRefusal),
        { InnerException: { } inner } => IsDepthRefusal(inner),
        _ => false,
    };

    /// <summary>The stored envelope, or <see langword="null"/> when it cannot be read as one this handler may run on.</summary>
    private static AlvoEvent? ReadableEvent(string payload, AlvoRunAs runAs)
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
            var message = HandlerMessage.For(@event, handler.Registration.Source);
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
    /// serialization failure, a deadlock — for as long as the retry would still start inside the lease. A fence that no
    /// longer holds is an answer (<see langword="false"/>), never retried; a stop ends the retries, and the lease returns
    /// the delivery.
    /// </summary>
    private async Task<bool> AnsweredAsync(Func<Task<bool>> answer, DeliveryAttempt attempt, AnswerWindow window)
    {
        for (var wait = FirstAnswerRetry; ; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LongestAnswerRetry.Ticks)))
        {
            try
            {
                return await answer().ConfigureAwait(false);
            }
            catch (Exception failure) when (IsStorageRefusal(failure) && window.Admits(time.GetUtcNow() + wait))
            {
                DeliveryLog.DeliveryAnswerRetried(logger, attempt.Delivery.Id, attempt.Delivery.Subscriber, TypeOf(failure), wait);
            }

            await Task.Delay(wait, time, window.Stopping).ConfigureAwait(false);
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

    /// <summary>Until when an answer may still be retried, and the stop that ends the retries sooner.</summary>
    private readonly record struct AnswerWindow(DateTimeOffset AnswerBy, CancellationToken Stopping)
    {
        internal bool Admits(DateTimeOffset retryAt) => retryAt < AnswerBy && !Stopping.IsCancellationRequested;
    }

    internal readonly record struct AttemptVerdict(string? ErrorType, bool Retryable)
    {
        internal static AttemptVerdict Success => new(null, false);

        internal bool Succeeded => ErrorType is null;

        internal static AttemptVerdict Retry(string errorType) => new(errorType, true);

        internal static AttemptVerdict Dead(string errorType) => new(errorType, false);
    }
}
