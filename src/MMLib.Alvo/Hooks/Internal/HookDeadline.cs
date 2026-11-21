namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// One run's deadline on the host's clock: the time it was given, how long it has run, and a one-way extension for the
/// cold-start allowance a first call through the transaction earns (#519).
/// </summary>
internal sealed class HookDeadline : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly Lock _gate = new();
    private TimeSpan _budget;
    private bool _disposed;

    internal HookDeadline(TimeSpan budget, TimeProvider time)
    {
        _budget = budget;
        _time = time;
        _started = time.GetTimestamp();
        _source = new CancellationTokenSource(budget, time);
    }

    internal CancellationToken Token => _source.Token;

    internal bool Passed => _source.IsCancellationRequested;

    internal TimeSpan Elapsed => _time.GetElapsedTime(_started);

    /// <summary>Gets the time the run has now; it only grows.</summary>
    internal TimeSpan Budget
    {
        get
        {
            lock (_gate)
            {
                return _budget;
            }
        }
    }

    /// <summary>
    /// Gives the run <paramref name="budget"/> from its start, if that is longer than it has and its deadline has not yet
    /// fired: a deadline that fired stays fired, and a run past the new budget is cancelled at once.
    /// </summary>
    internal void ExtendTo(TimeSpan budget)
    {
        lock (_gate)
        {
            if (_disposed || budget <= _budget || _source.IsCancellationRequested)
            {
                return;
            }

            _budget = budget;
            _source.CancelAfter(TimeSpan.FromTicks(Math.Max(0, (budget - Elapsed).Ticks)));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _source.Dispose();
    }
}
