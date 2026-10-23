using MMLib.Alvo.Events.Internal;

using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// Counts <c>alvo.delivery.dead</c> increments for one subscriber. The meter is static and shared by every fact running
/// in parallel, so the count is filtered on a subscriber id the fact owns.
/// </summary>
internal sealed class DeadDeliveries : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly string _subscriber;
    private long _count;

    internal DeadDeliveries(string subscriber)
    {
        _subscriber = subscriber;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AlvoEventMetrics.MeterName && instrument.Name == AlvoDeliveryMetrics.DeadName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(Record);
        _listener.Start();
    }

    internal long Count => Interlocked.Read(ref _count);

    public void Dispose() => _listener.Dispose();

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == AlvoDeliveryMetrics.SubscriberTag && Equals(tag.Value, _subscriber))
            {
                Interlocked.Add(ref _count, value);
            }
        }
    }
}
