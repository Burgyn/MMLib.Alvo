using Microsoft.JSInterop;

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
/// <b>Counted in the page, idempotent here.</b> <c>alvo.lockScroll</c> keeps a count, so two overlays open at
/// once release the page only when both have closed; this holds at most one of those counts, so a second hold
/// or release from the same overlay changes nothing.
/// </para>
/// </remarks>
/// <param name="js">The circuit's JavaScript runtime.</param>
internal sealed class ScrollLock(IJSRuntime js)
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
    /// Guarded because a circuit can be disposed mid-call — a reload, a navigation, a dropped connection — and
    /// a JS call on a dead circuit throws where nobody can act on it.
    /// </summary>
    private async Task SetAsync(bool locked)
    {
        try
        {
            await js.InvokeVoidAsync("alvo.lockScroll", locked);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
            /* The same death, reported by the call rather than the circuit: a reload tears the circuit down
               while the release is in flight. */
        }
    }
}
