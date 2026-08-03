using MMLib.Alvo.Testing.Events;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Sums every <see cref="long"/> measurement one meter publishes, per instrument, from the moment it is
/// constructed until it is disposed.
/// </summary>
/// <remarks>
/// <para>
/// <b>BCL only, deliberately.</b> <see cref="MeterListener"/> is in the base class library, so the criteria
/// suite reads real counters without this repository taking a dependency on
/// <c>Microsoft.Extensions.Diagnostics.Testing</c> — a test-only package added for one assertion is a
/// dependency every consumer of the test-support library would inherit.
/// </para>
/// <para>
/// <b>It sums rather than remembering the last measurement</b>, because the criterion is a count: "one
/// increment per filtered event" is a statement about the total. <see cref="CountOf"/> answers zero for an
/// instrument nobody published, which is what makes a renamed instrument or a counter created on a second
/// meter fail a criterion that expects a non-zero count instead of passing it silently.
/// </para>
/// <para>
/// <b>It has to be disposed.</b> The event counters are process-wide statics, so a listener left running keeps
/// summing measurements from every later test in the assembly — which is how one world's exact count becomes
/// another world's.
/// </para>
/// </remarks>
internal sealed class RecordingMeterListener : IAlvoEventMeter, IDisposable
{
    /// <summary>Starts listening to <paramref name="meterName"/>'s instruments, and to no others.</summary>
    /// <param name="meterName">The meter to listen to.</param>
    /// <remarks>
    /// <see cref="MeterListener.Start"/> replays every instrument that already exists, so a static metrics
    /// class whose counters were created by an earlier test is subscribed to exactly like one created later.
    /// </remarks>
    internal RecordingMeterListener(string meterName)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscriber) => Subscribe(instrument, subscriber, meterName),
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) => Record(instrument, measurement));
        _listener.Start();
    }

    /// <inheritdoc/>
    public long CountOf(string instrumentName) => _totals.TryGetValue(instrumentName, out var total) ? total : 0;

    /// <inheritdoc/>
    public void Dispose() => _listener.Dispose();

    private static void Subscribe(Instrument instrument, MeterListener listener, string meterName)
    {
        if (string.Equals(instrument.Meter.Name, meterName, StringComparison.Ordinal))
        {
            listener.EnableMeasurementEvents(instrument);
        }
    }

    private void Record(Instrument instrument, long measurement) =>
        _totals.AddOrUpdate(instrument.Name, measurement, (_, total) => total + measurement);

    private readonly ConcurrentDictionary<string, long> _totals = new(StringComparer.Ordinal);

    private readonly MeterListener _listener;
}
