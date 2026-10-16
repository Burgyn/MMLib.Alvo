namespace MMLib.Alvo;

/// <summary>
/// The provenance of the flow the current code runs in, carried by the .NET execution context, so a fresh
/// <see cref="AlvoContext"/> built inside a delivery or a timer firing still reports the chain it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, and set only by the framework's own pumps and write unit</b> — <c>MMLib.Alvo</c> and the EF driver.
/// Every assembly Abstractions grants <c>InternalsVisibleTo</c> can reach <see cref="Enter"/>, so an architecture fact
/// in each other granted product assembly's suite holds that it never does (spec 2026-10-14 §4.6, R-9). HX-01 ships no
/// production caller of <see cref="Enter"/>; HX-03 (deliveries) and HX-07 (the write unit) add them.
/// </para>
/// <para>
/// <b>The limit, stated:</b> it follows the execution context. Work a host hands to its own queue or background loop,
/// or runs with flow suppressed, leaves its reach.
/// </para>
/// </remarks>
internal static class AlvoFlow
{
    private static readonly AsyncLocal<AlvoProvenance?> _provenance = new();

    /// <summary>The ambient provenance, or <see langword="null"/> outside any framework flow.</summary>
    internal static AlvoProvenance? Provenance => _provenance.Value;

    /// <summary>Makes <paramref name="provenance"/> ambient until the returned scope is disposed.</summary>
    /// <param name="provenance">The provenance of the flow being entered.</param>
    /// <returns>A scope whose disposal restores whatever was ambient before.</returns>
    internal static IDisposable Enter(AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var previous = _provenance.Value;
        _provenance.Value = provenance;

        return new Restore(previous);
    }

    private sealed class Restore(AlvoProvenance? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _provenance.Value = previous;
        }
    }
}
