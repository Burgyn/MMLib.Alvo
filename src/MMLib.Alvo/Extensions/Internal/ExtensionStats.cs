using Microsoft.Extensions.Hosting;

using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Management;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace MMLib.Alvo.Extensions.Internal;

/// <summary>
/// How each extension ran in this process over the last day, read off the delivery duration measurements with a
/// <see cref="MeterListener"/> and kept in 24 hourly buckets per extension.
/// </summary>
/// <remarks>
/// <para>
/// <b>"This instance" is this process.</b> Alvo's meters are process-wide, so two hosts in one process — a test run — see
/// each other's runs; another replica's are never seen. Nothing is stored: a restart starts the day over, and
/// <see cref="ManagementExtensionStats.Since"/> says from when.
/// </para>
/// <para>
/// <b>One measurement carries everything:</b> <c>alvo.delivery.duration</c> is recorded once per answered attempt with the
/// subscriber and the outcome, so a run is a measurement, a failure is an outcome of <c>failed</c> or <c>dead</c>, and the
/// percentiles come from the same values. Write hooks' rejections join when write hooks exist; a handler refuses nothing.
/// </para>
/// <para>
/// <b>Bounded memory.</b> The listener sees every host's subscribers in the process, so at most
/// <see cref="MostExtensions"/> are tracked and a later one is ignored; an hour's latency bins are allocated on its first
/// run. The listener callback runs on any thread: the map is concurrent and each extension's buckets take one short lock.
/// </para>
/// </remarks>
internal sealed class ExtensionStats : IHostedService, IDisposable
{
    internal const string Scope = "this-instance";

    internal const int MostExtensions = 256;

    private const int Hours = 24;

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;
    private readonly ConcurrentDictionary<string, Buckets> _extensions = new(StringComparer.Ordinal);
    private readonly MeterListener _listener;
    private volatile bool _full;

    public ExtensionStats(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _started = time.GetUtcNow();
        _listener = new MeterListener { InstrumentPublished = Publish };
        _listener.SetMeasurementEventCallback<double>(OnDuration);
        _listener.Start();
    }

    internal static TimeSpan Window { get; } = TimeSpan.FromHours(Hours);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _listener.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _listener.Dispose();

    internal void Record(string subscriber, double milliseconds, bool failed) =>
        Track(subscriber)?.Record(HourOf(_time.GetUtcNow()), milliseconds, failed);

    internal ManagementExtensionStats Report(IReadOnlyList<string> ids)
    {
        var hour = HourOf(_time.GetUtcNow());
        var oldest = DateTimeOffset.FromUnixTimeSeconds((hour - Hours + 1) * 3600);

        return new ManagementExtensionStats
        {
            Scope = Scope,
            Window = Window,
            Since = oldest > _started ? oldest : _started,
            Items = [.. ids.Select(id => Stat(id, hour))],
        };
    }

    private ManagementExtensionStat Stat(string id, long hour) =>
        _extensions.TryGetValue(id, out var buckets) ? buckets.Stat(id, hour) : new ManagementExtensionStat { Id = id };

    private Buckets? Track(string subscriber)
    {
        if (_extensions.TryGetValue(subscriber, out var buckets))
        {
            return buckets;
        }

        if (_full || (_full = _extensions.Count >= MostExtensions))
        {
            return null;
        }

        return _extensions.GetOrAdd(subscriber, _ => new Buckets());
    }

    private static void Publish(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name == AlvoEventMetrics.MeterName && instrument.Name == AlvoDeliveryMetrics.DurationName)
        {
            listener.EnableMeasurementEvents(instrument);
        }
    }

    private void OnDuration(Instrument instrument, double milliseconds, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        string? subscriber = null;
        string? outcome = null;
        foreach (var tag in tags)
        {
            subscriber = tag.Key == AlvoDeliveryMetrics.SubscriberTag ? tag.Value as string : subscriber;
            outcome = tag.Key == AlvoDeliveryMetrics.OutcomeTag ? tag.Value as string : outcome;
        }

        if (subscriber is not null)
        {
            Record(subscriber, milliseconds, outcome is "failed" or "dead");
        }
    }

    private static long HourOf(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / 3600;

    private sealed class Buckets
    {
        private readonly Lock _gate = new();
        private readonly long[] _hours = Enumerable.Repeat(long.MinValue, Hours).ToArray();
        private readonly long[] _runs = new long[Hours];
        private readonly long[] _failures = new long[Hours];
        private readonly long[]?[] _bins = new long[]?[Hours];

        internal void Record(long hour, double milliseconds, bool failed)
        {
            lock (_gate)
            {
                var slot = Slot(hour);
                _runs[slot]++;
                _failures[slot] += failed ? 1 : 0;
                (_bins[slot] ??= new long[LatencyHistogram.Bins])[LatencyHistogram.Bin(milliseconds)]++;
            }
        }

        internal ManagementExtensionStat Stat(string id, long hour)
        {
            lock (_gate)
            {
                var live = Enumerable.Range(0, Hours).Where(slot => _hours[slot] > hour - Hours).ToList();
                var bins = new long[LatencyHistogram.Bins];
                live.ForEach(slot => Add(bins, _bins[slot]));
                var runs = live.Sum(slot => _runs[slot]);

                return new ManagementExtensionStat
                {
                    Id = id,
                    Invocations = runs,
                    Failures = live.Sum(slot => _failures[slot]),
                    P50 = LatencyHistogram.Percentile(bins, runs, 0.5),
                    P95 = LatencyHistogram.Percentile(bins, runs, 0.95),
                };
            }
        }

        private int Slot(long hour)
        {
            var slot = (int)(hour % Hours);
            if (_hours[slot] != hour)
            {
                _hours[slot] = hour;
                _runs[slot] = 0;
                _failures[slot] = 0;
                Array.Clear(_bins[slot] ?? []);
            }

            return slot;
        }

        private static void Add(long[] total, long[]? bins)
        {
            for (var bin = 0; bins is not null && bin < bins.Length; bin++)
            {
                total[bin] += bins[bin];
            }
        }
    }
}
