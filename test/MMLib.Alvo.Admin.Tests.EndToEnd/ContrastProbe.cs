using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Measures the WCAG contrast of a control's label against what is painted behind it, as the browser resolved both.
/// </summary>
/// <remarks>
/// <para>
/// <b>Painted, not parsed.</b> A computed colour may come back as <c>rgb()</c>, <c>oklch()</c> or
/// <c>color(srgb …)</c>, so each is painted on a one-pixel canvas and read back. The background is composited: every
/// translucent one from the element up to the first opaque ancestor is laid over it, in that order, because a badge's
/// wash or a selected row's tint changes what the text is read against. A ghost button is transparent, so its label is
/// read against the bar or the panel it sits on. An icon-only control is read by its <c>currentColor</c>, which is
/// what its stroke paints.
/// </para>
/// <para>
/// Every running animation is waited out first: a button eases from its disabled colours to its own, and the pane
/// slides in, so a pair read mid-transition is neither.
/// </para>
/// </remarks>
internal static class ContrastProbe
{
    /// <summary>AA for normal-size text (WCAG 2.2, 1.4.3).</summary>
    public const double AA = 4.5;

    private const string Script = """
        (elements) => {
          const context = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
          const paint = v => { context.clearRect(0, 0, 1, 1); context.fillStyle = v; context.fillRect(0, 0, 1, 1);
            return [...context.getImageData(0, 0, 1, 1).data]; };
          const lum = ([r, g, b]) => [r, g, b].map(c => { c /= 255; return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; })
            .reduce((sum, c, i) => sum + c * [0.2126, 0.7152, 0.0722][i], 0);
          const behind = e => {
            const layers = [];
            let base = paint(getComputedStyle(document.body).backgroundColor);
            for (let p = e; p; p = p.parentElement) {
              const c = paint(getComputedStyle(p).backgroundColor);
              if (c[3] === 255) { base = c; break; }
              if (c[3] > 0) layers.push(c);
            }
            const [r, g, b] = layers.reverse().reduce((under, [lr, lg, lb, la]) => {
              const a = la / 255; return [lr * a + under[0] * (1 - a), lg * a + under[1] * (1 - a), lb * a + under[2] * (1 - a)];
            }, base.slice(0, 3));
            return `rgb(${Math.round(r)}, ${Math.round(g)}, ${Math.round(b)})`;
          };
          const name = e => (e.getAttribute('aria-label') || e.innerText || '').trim().replace(/\s+/g, ' ');
          return elements.map(e => {
            const color = getComputedStyle(e).color, background = behind(e);
            const [a, b] = [lum(paint(color)), lum(paint(background))];
            return { Name: name(e), Ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05), Color: color, Background: background };
          });
        }
        """;

    /// <summary>The contrast of each element <paramref name="controls"/> matches.</summary>
    /// <param name="controls">The controls, which must be attached.</param>
    /// <returns>One reading per element, in document order.</returns>
    public static async Task<IReadOnlyList<Reading>> ReadAsync(ILocator controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        await controls.Page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState !== 'running')").ConfigureAwait(false);
        return await controls.EvaluateAllAsync<Reading[]>(Script).ConfigureAwait(false);
    }

    /// <summary>Every enabled, visible button and link on the page, the library's included.</summary>
    /// <param name="page">A settled page of the dashboard.</param>
    /// <returns>The locator.</returns>
    public static ILocator Controls(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Locator("button:not(:disabled):visible, a[href]:visible");
    }

    /// <summary>One control's contrast, with the two colours it was measured between.</summary>
    /// <remarks>Settable properties, because Playwright builds a returned object with a parameterless constructor.</remarks>
    internal sealed class Reading
    {
        /// <summary>Its accessible name, or its text.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The WCAG ratio.</summary>
        public double Ratio { get; set; }

        /// <summary>The label's colour.</summary>
        public string Color { get; set; } = string.Empty;

        /// <summary>What it is read against.</summary>
        public string Background { get; set; } = string.Empty;

        /// <inheritdoc />
        public override string ToString() => $"'{Name}' {Ratio:0.00}:1, {Color} on {Background}";
    }
}
