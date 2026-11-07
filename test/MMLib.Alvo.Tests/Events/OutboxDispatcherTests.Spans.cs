using MMLib.Alvo.Events.Internal;

using System.Collections.Concurrent;
using System.Diagnostics;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The outbox only fans out: a descriptor action's consumer span on <c>MMLib.Alvo.Hooks</c> is opened by its own delivery's
/// run, never by the dispatcher (spec 2026-10-14 §4.14).
/// </summary>
public sealed partial class OutboxDispatcherTests
{
    [Fact]
    public async Task The_outbox_opens_no_delivery_span_because_each_action_is_its_own_delivery()
    {
        using var spans = new DeliverySpans();
        var deliveries = new FakeDeliveryStore(TimeProvider.System);
        var pump = Subject(StoreWith(WonDeal()), Ready, fanOut: FanOutTo(deliveries));

        await spans.WithinAsync(() => pump.PumpOneBatchAsync(Cancellation));

        spans.Recorded.ShouldBeEmpty();
        deliveries.Rows.ShouldContain(row => row.Kind == "action" && row.Subscriber.StartsWith("/entities/deals/hooks/afterUpdate#", StringComparison.Ordinal));
    }

    [Fact]
    public void The_source_is_the_one_the_spec_names() => AlvoHookTelemetry.SourceName.ShouldBe("MMLib.Alvo.Hooks");

    /// <summary>
    /// Listens to the hook source, keeping only spans under this fact's own root, so facts running in parallel never
    /// count each other's spans.
    /// </summary>
    private sealed class DeliverySpans : IDisposable
    {
        private const string TestSource = "MMLib.Alvo.Tests.DeliverySpans";

        private readonly ActivitySource _test = new(TestSource);
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;
        private ActivityTraceId _trace;

        internal DeliverySpans()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name is AlvoHookTelemetry.SourceName or TestSource,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Keep,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        internal IReadOnlyList<Activity> Recorded => [.. _stopped];

        internal async Task WithinAsync(Func<Task> act)
        {
            var root = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
            using var scope = _test.StartActivity("fact", ActivityKind.Internal, root);
            _trace = root.TraceId;
            await act();
        }

        public void Dispose()
        {
            _listener.Dispose();
            _test.Dispose();
        }

        private void Keep(Activity activity)
        {
            if (activity.Source.Name == AlvoHookTelemetry.SourceName && activity.TraceId == _trace)
            {
                _stopped.Enqueue(activity);
            }
        }
    }
}
