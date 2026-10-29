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
/// production caller of <see cref="Enter"/>; HX-03 (deliveries) and HX-07 (the write unit) add them. HX-07 adds the
/// open transaction unit beside the provenance: <see cref="EnterUnit"/> is called by the EF driver's
/// <c>TransactAsync</c> alone, and every data-port entry calls <see cref="RefuseInsideUnit"/>.
/// </para>
/// <para>
/// <b>The limit, stated:</b> it follows the execution context. Work a host hands to its own queue or background loop,
/// or runs with flow suppressed, leaves its reach.
/// </para>
/// </remarks>
internal static class AlvoFlow
{
    private static readonly AsyncLocal<AlvoProvenance?> _provenance = new();
    private static readonly AsyncLocal<IAlvoFlowUnit?> _unit = new();

    /// <summary>The ambient provenance, or <see langword="null"/> outside any framework flow.</summary>
    internal static AlvoProvenance? Provenance => _provenance.Value;

    /// <summary>The transaction unit open on this flow, or <see langword="null"/> when none is — including one whose <c>TransactAsync</c> has returned.</summary>
    /// <remarks>
    /// An ended unit is not reported, although work its delegate started may still carry it in its execution context:
    /// such work runs after the unit and may use the data port.
    /// </remarks>
    internal static IAlvoFlowUnit? Unit => _unit.Value is { IsOpen: true } open ? open : null;

    /// <summary>Makes <paramref name="provenance"/> ambient until the returned scope is disposed; the open unit, if any, stays.</summary>
    /// <param name="provenance">The provenance of the flow being entered.</param>
    /// <returns>A scope whose disposal restores whatever was ambient before.</returns>
    internal static IDisposable Enter(AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var restore = new Restore(_provenance.Value, _unit.Value);
        _provenance.Value = provenance;

        return restore;
    }

    /// <summary>Makes <paramref name="unit"/> the open unit, with <paramref name="provenance"/>, until the returned scope is disposed.</summary>
    /// <param name="unit">The unit being opened.</param>
    /// <param name="provenance">The unit's provenance, which every write inside it reports.</param>
    /// <returns>A scope whose disposal restores whatever was ambient before.</returns>
    internal static IDisposable EnterUnit(IAlvoFlowUnit unit, AlvoProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(provenance);
        var restore = new Restore(_provenance.Value, _unit.Value);
        _provenance.Value = provenance;
        _unit.Value = unit;

        return restore;
    }

    /// <summary>Refuses a data-port call made while a unit is open on this flow, and fails that unit.</summary>
    /// <param name="call">The refused member, as <c>IAlvoData.CreateAsync</c>.</param>
    /// <param name="keyed">Whether the call carried an idempotency key, which is refused as an argument instead.</param>
    /// <exception cref="Data.AlvoUnitInProgressException">A unit is open and the call carried no key.</exception>
    /// <exception cref="ArgumentException">A unit is open and the call carried a key.</exception>
    internal static void RefuseInsideUnit(string call, bool keyed = false)
    {
        if (Unit is not { } unit)
        {
            return;
        }

        Exception refusal = keyed ? KeyInsideUnit(call) : Data.AlvoUnitInProgressException.For(call);
        unit.Poison(refusal);
        throw refusal;
    }

#pragma warning disable CA2208 // "idempotency" is the IAlvoData write's parameter, the one the caller handed in.
    private static ArgumentException KeyInsideUnit(string call) => new(
        $"{call} carried an idempotency key while a transaction unit is open on this flow, so it was refused and the unit "
        + "will roll back. A unit is never replayed, so a key inside it guards nothing; a command's idempotency belongs to "
        + "the command that opens the unit. Fix: drop the key and make the call through the unit's IAlvoTransaction.",
        "idempotency");
#pragma warning restore CA2208

    private sealed class Restore(AlvoProvenance? provenance, IAlvoFlowUnit? unit) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _provenance.Value = provenance;
            _unit.Value = unit;
        }
    }
}
