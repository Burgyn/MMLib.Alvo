using MMLib.Alvo.Events.Internal;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Every after-hook action the dispatcher runs is one consumer span on <c>MMLib.Alvo.Hooks</c>, tagged with ids only
/// (spec 2026-10-14 §4.14) — what the test host's hook recorder and a host's OpenTelemetry pipeline both read.
/// </summary>
public sealed partial class OutboxDispatcherTests
{
    [Fact]
    public async Task Each_after_hook_run_is_one_delivery_span_tagged_with_its_subscriber()
    {
        using var spans = new DeliverySpans();
        var deal = WonDeal();
        var pump = Subject(StoreWith(deal), Ready);

        await spans.WithinAsync(() => pump.PumpOneBatchAsync(Cancellation));

        var span = spans.Recorded.ShouldHaveSingleItem();
        span.OperationName.ShouldBe("alvo.delivery");
        span.DisplayName.ShouldBe($"alvo.delivery {DealsHook}");
        span.Kind.ShouldBe(ActivityKind.Consumer);
        span.GetTagItem("alvo.subscriber").ShouldBe(DealsHook);
        span.GetTagItem("alvo.event.id").ShouldBe(deal.Id.ToString());
        span.GetTagItem("alvo.event.type").ShouldBe("entity.deals.updated");
        span.GetTagItem("alvo.outcome").ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_failed_run_is_a_failed_span()
    {
        using var spans = new DeliverySpans();
        var pump = Subject(StoreWith(WonDeal()), Ready, status: HttpStatusCode.InternalServerError);

        await spans.WithinAsync(() => pump.PumpOneBatchAsync(Cancellation));

        var span = spans.Recorded.ShouldHaveSingleItem();
        span.GetTagItem("alvo.outcome").ShouldBe("failed");
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    /// <summary>
    /// A run cut short by the host stopping is neither outcome: the dispatcher does not count a shutdown as a failed
    /// attempt, and the span the hook recorder reads must not either.
    /// </summary>
    [Fact]
    public async Task A_run_cut_short_by_shutdown_carries_no_outcome()
    {
        using var spans = new DeliverySpans();
        var store = StoreWith(WonDeal());
        var blocked = new TaskCompletionSource();

        await spans.WithinAsync(async () =>
        {
            await using var pump = await StartAsync(store, Ready, blockDeliveriesOn: blocked);
            await UntilAsync(() => store.ClaimCount > 0, "the delivery to be in flight");
            await pump.MeasureStopAsync();
        });

        var span = spans.Recorded.ShouldHaveSingleItem();
        span.GetTagItem("alvo.outcome").ShouldBeNull();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task An_event_that_matches_no_hook_opens_no_span()
    {
        using var spans = new DeliverySpans();
        var pump = Subject(StoreWith(Event("entity.vehicles.created")), Ready);

        await spans.WithinAsync(() => pump.PumpOneBatchAsync(Cancellation));

        spans.Recorded.ShouldBeEmpty();
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
