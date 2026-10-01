namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// One overlay's hold on the page behind it: while held, the page does not scroll.
/// </summary>
/// <remarks>
/// <para>
/// <b>The scroll lock is what makes an overlay feel like one.</b> Without it a drag near a bottom sheet's edge
/// scrolls the list underneath, and the sheet appears to float over a page that is still moving. The sheet and
/// the command palette both take it, so the palette is no longer a second modal with its own rules
/// (docs/architecture/admin-dashboard-review.md, F-26).
/// </para>
/// <para>
/// <b>Counted in the page, idempotent here.</b> admin.js's <c>lockScroll</c> keeps a count, so two overlays open at
/// once release the page only when both have closed; this holds at most one of those counts, so a second hold
/// or release from the same overlay changes nothing.
/// </para>
/// </remarks>
/// <param name="interop">The circuit's way into admin.js.</param>
internal sealed class ScrollLock(AdminInterop interop)
{
    /// <summary>Whether this overlay is holding the page.</summary>
    public bool Held { get; private set; }

    /// <summary>Holds the page, once.</summary>
    public async Task HoldAsync()
    {
        if (!Held)
        {
            Held = true;
            await SetAsync(true);
        }
    }

    /// <summary>Lets the page go, once.</summary>
    public async Task ReleaseAsync()
    {
        if (Held)
        {
            Held = false;
            await SetAsync(false);
        }
    }

    /// <summary>
    /// Through <see cref="AdminInterop"/>, whose disconnect policy covers the circuit that goes mid-call — a
    /// reload tears it down while the release is in flight.
    /// </summary>
    private Task SetAsync(bool locked) => interop.LockScrollAsync(locked);
}
