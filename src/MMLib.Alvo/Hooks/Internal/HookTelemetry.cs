using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Rules;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One <c>alvo.hook</c> span per run on <c>MMLib.Alvo.Hooks</c> — its id in <c>alvo.subscriber</c>, the tag the test host's
/// recorder reads (Ruling 27) — and the hook meters of spec 2026-10-14 §4.14.
/// </summary>
internal static class HookTelemetry
{
    internal const string MeterName = "MMLib.Alvo.Hooks";
    internal const string SpanName = "alvo.hook";
    internal const string DurationName = "alvo.hook.duration";
    internal const string RejectionsName = "alvo.hook.rejections";
    internal const string OperationsName = "alvo.hook.operations";
    internal const string SystemAccessName = "alvo.hook.system_access";
    internal const string SubscriberTag = AlvoHookTelemetry.SubscriberTag;
    internal const string OutcomeTag = AlvoHookTelemetry.OutcomeTag;
    internal const string Succeeded = "succeeded";
    internal const string Rejected = "rejected";
    internal const string Failed = "failed";

    private static readonly ActivitySource _source = new(AlvoHookTelemetry.SourceName);
    private static readonly Meter _meter = new(MeterName);
    private static readonly Histogram<double> _duration = _meter.CreateHistogram<double>(DurationName, "ms", "How long one write-hook run took.");
    private static readonly Counter<long> _rejections = _meter.CreateCounter<long>(RejectionsName, description: "Write-hook runs that rejected the write.");
    private static readonly Counter<long> _operations = _meter.CreateCounter<long>(OperationsName, description: "Calls write hooks made through the write's transaction.");
    private static readonly Counter<long> _systemAccess = _meter.CreateCounter<long>(SystemAccessName, description: "Times a write hook acted as the system.");

    internal static Activity? Start(string id, WriteInvocation invocation, WriteHookStage stage)
    {
        var activity = _source.StartActivity(SpanName, ActivityKind.Internal);
        if (activity is { IsAllDataRequested: true })
        {
            activity.DisplayName = $"{SpanName} {id}";
            activity.SetTag(SubscriberTag, id);
            activity.SetTag("alvo.entity", invocation.Entity);
            activity.SetTag("alvo.operation", invocation.Operation.ToString().ToLowerInvariant());
            activity.SetTag("alvo.stage", WriteHookBinding.KindOf(stage));
        }

        return activity;
    }

    internal static void Finish(Activity? activity, string id, string outcome, TimeSpan elapsed, int operations)
    {
        var subscriber = new KeyValuePair<string, object?>(SubscriberTag, id);
        _duration.Record(elapsed.TotalMilliseconds, subscriber, new KeyValuePair<string, object?>(OutcomeTag, outcome));
        if (operations > 0)
        {
            _operations.Add(operations, subscriber);
        }

        if (outcome == Rejected)
        {
            _rejections.Add(1, subscriber);
        }

        activity?.SetTag(OutcomeTag, outcome);
        if (outcome == Failed)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
    }

    internal static void SystemAccess(string id) => _systemAccess.Add(1, new KeyValuePair<string, object?>(SubscriberTag, id));
}
