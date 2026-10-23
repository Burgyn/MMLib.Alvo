using System.Collections.Concurrent;
using System.Diagnostics;

namespace MMLib.Alvo.TestHost;

/// <summary>
/// Records which hooks and subscribers ran inside one <see cref="AlvoTestHost"/>, read off Alvo's
/// <c>MMLib.Alvo.Hooks</c> activity source.
/// </summary>
/// <remarks>
/// <para>
/// In this build what runs there is a descriptor after-hook's action during <see cref="AlvoTestHost.DrainEventsAsync"/>,
/// named by its JSON pointer, such as <c>/entities/tickets/hooks/afterCreate/0</c>; host-code extensions join under
/// their own ids as they ship. A run counts unless it failed: a failed attempt is not a run, a run cut short by the host
/// stopping (no outcome) is not either, and any other outcome — a write hook that rejects, from HX-09 on — is.
/// </para>
/// <para>
/// The activity source is process-wide, so the recorder samples and keeps only what runs inside its own host's drains:
/// several hosts in one test run never see each other's runs, and a span outside them is left to whoever else listens.
/// </para>
/// </remarks>
#pragma warning disable CA1001 // The owning AlvoTestHost stops the listener on dispose; a public Dispose would let a test stop it early.
public sealed class AlvoHookRecorder
#pragma warning restore CA1001
{
    internal const string HooksSourceName = "MMLib.Alvo.Hooks";
    internal const string DrainSourceName = "MMLib.Alvo.TestHost";

    private const string SubscriberTag = "alvo.subscriber";
    private const string OutcomeTag = "alvo.outcome";
    private const string Failed = "failed";
    private const string NoOutcome = "no outcome";

    private readonly ConcurrentDictionary<ActivityTraceId, byte> _drains = new();
    private readonly ConcurrentQueue<(string Id, string? Outcome)> _runs = new();
    private readonly ActivityListener _listener;

    internal AlvoHookRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is HooksSourceName or DrainSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => _drains.ContainsKey(options.TraceId)
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None,
            ActivityStopped = Record,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Asserts that <paramref name="id"/> ran exactly <paramref name="times"/> times, failed attempts not counted.</summary>
    /// <param name="id">The hook or subscriber id; for a descriptor after-hook, its JSON pointer.</param>
    /// <param name="times">How many runs are expected; <c>0</c> asserts it never ran.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="times"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">It ran a different number of times; the message lists every run.</exception>
    public void AssertRan(string id, int times = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(times);

        var ran = _runs.Count(run => run.Id == id && run.Outcome is { } outcome && outcome != Failed);
        if (ran != times)
        {
            throw Mismatch(id, times, ran);
        }
    }

    /// <summary>Claims every activity of the drain whose root trace is <paramref name="drain"/> for this host.</summary>
    internal void Track(ActivityTraceId drain) => _drains.TryAdd(drain, 0);

    /// <summary>Stops listening.</summary>
    internal void Stop() => _listener.Dispose();

    private void Record(Activity activity)
    {
        if (activity.Source.Name == HooksSourceName
            && _drains.ContainsKey(activity.TraceId)
            && activity.GetTagItem(SubscriberTag) is string id)
        {
            _runs.Enqueue((id, activity.GetTagItem(OutcomeTag) as string));
        }
    }

    private InvalidOperationException Mismatch(string id, int expected, int actual) => new(
        $"Expected '{id}' to run {expected} time(s), but it ran {actual}. Recorded runs: {Described()}.");

    private string Described() =>
        _runs.IsEmpty ? "none" : string.Join(", ", _runs.Select(run => $"{run.Id} ({run.Outcome ?? NoOutcome})"));
}
