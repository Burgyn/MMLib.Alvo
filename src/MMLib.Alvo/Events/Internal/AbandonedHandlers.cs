using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The handler invocations the runner abandoned past their timeout and settle grace that are still running, per handler
/// id — and the stand-down that bounds them: a handler with its pool's cap of them alive — <see cref="AlvoDeliveryOptions.MaxConcurrency"/>,
/// or <see cref="AlvoDeliveryOptions.MaxJobConcurrency"/> for a scheduled job — is not claimed for until one of them ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a stand-down.</b> .NET cannot abort a thread, so an abandoned handler that blocks its thread and ignores its token
/// keeps a thread-pool thread until it returns. Without a bound, every further claim of that handler could strand another
/// thread — up to <see cref="AlvoDeliveryOptions.MaxConcurrency"/> per timeout, across every record — until the pool
/// starves the whole process. Standing the one handler down keeps the others flowing.
/// </para>
/// <para>
/// <b>The bound is per handler and per host.</b> Claiming stops once the count reaches the cap; deliveries of that handler
/// already running at that moment may still be abandoned, so the count can reach at most
/// <c>2 × MaxConcurrency − 1</c> — and, with one delivery at a time, never more than the cap.
/// </para>
/// </remarks>
/// <param name="deliveries">The caps: <see cref="AlvoDeliveryOptions.MaxConcurrency"/> and <see cref="AlvoDeliveryOptions.MaxJobConcurrency"/>.</param>
/// <param name="logger">Where a handler entering the stand-down is recorded.</param>
internal sealed class AbandonedHandlers(IOptions<AlvoDeliveryOptions> deliveries, ILogger<AbandonedHandlers> logger)
{
    private readonly ConcurrentDictionary<string, int> _alive = new(StringComparer.Ordinal);

    /// <summary>Gets how many abandoned invocations of <paramref name="subscriber"/> are still running.</summary>
    internal int Alive(string subscriber) => _alive.TryGetValue(subscriber, out var alive) ? alive : 0;

    /// <summary>Whether <paramref name="subscriber"/> has as many abandoned invocations alive as its pool's cap, so it is not claimed for.</summary>
    /// <param name="subscriber">The handler or job id.</param>
    /// <param name="job">Whether it is a job, capped by the job pool.</param>
    internal bool StoodDown(string subscriber, bool job = false) => Alive(subscriber) >= Cap(job);

    /// <summary>Counts an abandoned invocation until it ends, and logs once when the count reaches its pool's cap.</summary>
    internal void Track(string subscriber, Task invocation, bool job = false)
    {
        var alive = _alive.AddOrUpdate(subscriber, 1, (_, count) => count + 1);
        if (alive == Cap(job))
        {
            DeliveryLog.HandlerStoodDown(logger, subscriber, alive);
        }

        _ = invocation.ContinueWith(
            _ => _alive.AddOrUpdate(subscriber, 0, (_, count) => count - 1),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private int Cap(bool job) => job ? deliveries.Value.MaxJobConcurrency : deliveries.Value.MaxConcurrency;
}
