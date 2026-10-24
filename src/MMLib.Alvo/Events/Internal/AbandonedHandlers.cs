using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using MMLib.Alvo.Extensions;

using System.Collections.Concurrent;

namespace MMLib.Alvo.Events.Internal;

/// <summary>
/// The handler invocations the runner abandoned past their timeout and settle grace that are still running, per handler
/// id — and the stand-down that bounds them: a handler with <see cref="AlvoDeliveryOptions.MaxConcurrency"/> of them alive
/// is not claimed for until one of them ends.
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
/// <param name="deliveries">The cap: <see cref="AlvoDeliveryOptions.MaxConcurrency"/>.</param>
/// <param name="logger">Where a handler entering the stand-down is recorded.</param>
internal sealed class AbandonedHandlers(IOptions<AlvoDeliveryOptions> deliveries, ILogger<AbandonedHandlers> logger)
{
    private readonly ConcurrentDictionary<string, int> _alive = new(StringComparer.Ordinal);

    /// <summary>Gets how many abandoned invocations of <paramref name="subscriber"/> are still running.</summary>
    internal int Alive(string subscriber) => _alive.TryGetValue(subscriber, out var alive) ? alive : 0;

    /// <summary>Whether <paramref name="subscriber"/> has as many abandoned invocations alive as the cap, so it is not claimed for.</summary>
    internal bool StoodDown(string subscriber) => Alive(subscriber) >= deliveries.Value.MaxConcurrency;

    /// <summary>Counts an abandoned invocation until it ends, and logs once when the count reaches the cap.</summary>
    internal void Track(string subscriber, Task invocation)
    {
        var alive = _alive.AddOrUpdate(subscriber, 1, (_, count) => count + 1);
        if (alive == deliveries.Value.MaxConcurrency)
        {
            DeliveryLog.HandlerStoodDown(logger, subscriber, alive);
        }

        _ = invocation.ContinueWith(
            _ => _alive.AddOrUpdate(subscriber, 0, (_, count) => count - 1),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
