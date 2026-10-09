using Microsoft.Playwright;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Waits for the page's address to match, without the race in Playwright's own <c>WaitForURLAsync</c>. Every scenario
/// waits for an address through this, never through <c>IPage.WaitForURLAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The race.</b> Playwright for .NET (1.56, and 1.63 still) answers <c>WaitForURLAsync</c> by reading
/// <c>Frame.Url</c> once and, when it does not match yet, subscribing to the frame's next <c>Navigated</c> event. The
/// event is dispatched on the transport's reader thread, not the test's, so a navigation that lands between the read
/// and the subscription updates <c>Url</c> unseen and is never heard: the wait runs out its sixty seconds on a page
/// already at the address. A dashboard navigation is a client-side route change a few milliseconds after the action
/// that made it, which is exactly that window. Measured in a kept trace: Enter in New entity at 1 251 ms, the wait's
/// subscription at 1 282 ms, the page at <c>/schema/gauges</c> by 1 287 ms, and the wait timed out at 61 282 ms.
/// It is the shape of every "waiting for navigation to … until Load" flake CI has shown (KeyboardConsistency,
/// RefusedRollback, SystemMap's Enter and zoom).
/// </para>
/// <para>
/// <b>The fix</b> reads <c>Url</c> again and again until it matches, so a navigation is seen whenever it lands, then
/// waits for the load state as Playwright's own wait does. The glob is Playwright's: <c>**</c> is anything, <c>*</c>
/// anything but a slash, and <c>?</c> is the question mark itself.
/// </para>
/// </remarks>
internal static class PageAddress
{
    private static readonly TimeSpan _poll = TimeSpan.FromMilliseconds(25);

    /// <summary>Waits until the page's address matches <paramref name="glob"/> and the page has loaded.</summary>
    /// <param name="page">The page.</param>
    /// <param name="glob">The address, as a Playwright glob.</param>
    /// <param name="timeout">How long to wait, in milliseconds.</param>
    /// <returns>A task that completes once the address matches.</returns>
    public static Task WaitForAddressAsync(this IPage page, string glob, float timeout = AdminWorld.ActionTimeout)
    {
        var pattern = Pattern(glob);
        return WaitForAddressAsync(page, url => pattern.IsMatch(url), glob, timeout);
    }

    /// <summary>Waits until <paramref name="matches"/> holds for the page's address and the page has loaded.</summary>
    /// <param name="page">The page.</param>
    /// <param name="matches">What the address must satisfy.</param>
    /// <param name="timeout">How long to wait, in milliseconds.</param>
    /// <returns>A task that completes once the address matches.</returns>
    public static Task WaitForAddressAsync(this IPage page, Func<string, bool> matches, float timeout = AdminWorld.ActionTimeout)
        => WaitForAddressAsync(page, matches, "the predicate", timeout);

    private static async Task WaitForAddressAsync(IPage page, Func<string, bool> matches, string described, float timeout)
    {
        ArgumentNullException.ThrowIfNull(page);
        var waited = Stopwatch.StartNew();
        while (!matches(page.Url))
        {
            if (page.IsClosed)
            {
                throw new PlaywrightException($"The page closed while waiting for {described}; it was at {page.Url}.");
            }

            if (waited.ElapsedMilliseconds > timeout)
            {
                throw new TimeoutException($"The page never reached {described} in {timeout} ms; it is at {page.Url}.");
            }

            await Task.Delay(_poll).ConfigureAwait(false);
        }

        var left = (float)Math.Max(1, timeout - waited.ElapsedMilliseconds);
        await page.WaitForLoadStateAsync(LoadState.Load, new() { Timeout = left }).ConfigureAwait(false);
    }

    /// <summary>A Playwright glob as a whole-address pattern.</summary>
    internal static Regex Pattern(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        var pattern = new StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            if (glob[index] != '*')
            {
                pattern.Append(Regex.Escape(glob[index].ToString()));
            }
            else if (index + 1 < glob.Length && glob[index + 1] == '*')
            {
                pattern.Append(".*");
                index++;
            }
            else
            {
                pattern.Append("[^/]*");
            }
        }

        return new Regex(pattern.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}
