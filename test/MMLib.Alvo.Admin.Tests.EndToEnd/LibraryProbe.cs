using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Measures what the component library's stylesheet changes about the elements Alvo draws.
/// </summary>
/// <remarks>
/// <para>
/// It reads the computed style of every element that is not the library's own. It reads it twice: with
/// <c>alvo-mud.css</c> enabled, then with that link disabled, which also disables the sheet it imports. The second
/// reading is the page as it was before the library was referenced, so any difference is a rule of the library's
/// reaching Alvo's markup. Being layered beneath Alvo is not enough to prevent that: a layered author rule still
/// beats the browser's own defaults. Mud's reset strips a paragraph's margins wherever alvo.css says nothing.
/// </para>
/// <para>
/// An element is the library's when a class of its own starts with <c>mud-</c>, the same test alvo.css's base
/// layer uses to scope its restore, or when it is the <c>legend</c> of a Mud fieldset: an outlined input with a
/// label draws its notch with a classless legend, which is the library's markup and never Alvo's. Properties an element inherits are compared only where no ancestor is the
/// library's: inside a Mud component, disabling the sheet changes what the component passes down, and that is not a
/// change to Alvo's rules. Chromium passes <c>user-select</c> down as well, so it is one of those. <c>display</c>
/// is compared except on a direct child of a library element, which a flex or grid parent blockifies.
/// </para>
/// <para>
/// <b>Sizes and margins are read as computed values, not as the used pixels <c>getComputedStyle</c> resolves
/// them to.</b> The shell is the library's layout, so with its sheet disabled the app bar and the drawer stop being
/// fixed and every column changes width; a used width then differs on every element for no rule of the library's.
/// The computed value (<c>auto</c>, <c>100%</c>, <c>12px</c>) is what a rule sets, and that is what this is for.
/// </para>
/// </remarks>
internal static class LibraryProbe
{
    private const string Script = """
        (named) => {
          const link = [...document.querySelectorAll('link[rel=stylesheet]')].find(l => l.href.includes('alvo-mud.css'));
          if (!link) return { Compared: 0, Differences: ['no alvo-mud.css link in the document'], Missing: named };
          const own = ['margin-top', 'margin-right', 'margin-bottom', 'margin-left',
            'padding-top', 'padding-right', 'padding-bottom', 'padding-left',
            'border-top-width', 'border-right-width', 'border-bottom-width', 'border-left-width',
            'border-top-style', 'border-right-style', 'border-bottom-style', 'border-left-style',
            'border-top-left-radius', 'border-bottom-right-radius', 'box-sizing', 'position',
            'vertical-align', 'align-items', 'justify-content', 'appearance', 'overflow-x',
            'overflow-y', 'outline-style', 'text-decoration-line', 'background-color', 'width', 'height'];
          const inherited = ['color', 'cursor', 'font-family', 'font-size', 'font-weight', 'line-height',
            'letter-spacing', 'text-transform', 'word-wrap', '-webkit-font-smoothing', '-webkit-tap-highlight-color',
            'user-select'];
          const computed = new Set(['width', 'height', 'margin-top', 'margin-right', 'margin-bottom', 'margin-left']);
          const isLibrary = e => (e.getAttribute('class') ?? '').split(/\s+/).some(c => c.startsWith('mud-'))
            || (e.tagName === 'LEGEND' && !!e.parentElement?.closest('fieldset[class*=mud-]'));
          const underLibrary = e => { for (let p = e.parentElement; p; p = p.parentElement) if (isLibrary(p)) return true; return false; };
          const elements = [...document.querySelectorAll('body, body *')].filter(e => !isLibrary(e));
          const keys = elements.map(e => (underLibrary(e) ? own : own.concat(inherited))
            .concat(e.parentElement && isLibrary(e.parentElement) ? [] : ['display']));
          const value = (s, map, k) => { if (computed.has(k)) { try { return String(map.get(k)); } catch { } } return s.getPropertyValue(k); };
          const read = () => elements.map((e, i) => {
            const s = getComputedStyle(e); const map = e.computedStyleMap();
            return keys[i].map(k => value(s, map, k)); });
          const withLibrary = read();
          link.disabled = true;
          const without = read();
          link.disabled = false;
          const describe = e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '')
            + [...e.classList].map(c => '.' + c).join('');
          const differences = new Set();
          elements.forEach((e, i) => keys[i].forEach((k, j) => {
            if (withLibrary[i][j] !== without[i][j]) differences.add(`${describe(e)} ${k}: ${withLibrary[i][j]}, before the library ${without[i][j]}`);
          }));
          return { Compared: elements.length, Differences: [...differences].slice(0, 60), Missing: named.filter(s => !document.querySelector(s)) };
        }
        """;

    /// <summary>
    /// Every computed-style difference the library makes to an element Alvo draws on the current page.
    /// </summary>
    /// <param name="page">A page of the dashboard, settled.</param>
    /// <param name="named">Selectors that must match on this page, so the comparison is known to cover them.</param>
    /// <returns>What was compared, what differed, and which named selectors matched nothing.</returns>
    public static async Task<Reading> ReadAsync(IPage page, params string[] named)
    {
        /* Transitions off: a value read mid-transition would differ from itself. */
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce }).ConfigureAwait(false);
        return await page.EvaluateAsync<Reading>(Script, named).ConfigureAwait(false);
    }

    /// <summary>One reading of a page; settable, because Playwright deserialises into a parameterless type.</summary>
    internal sealed class Reading
    {
        /// <summary>How many elements were compared.</summary>
        public int Compared { get; set; }

        /// <summary>Each difference, as <c>element property: now, before the library then</c>.</summary>
        public string[] Differences { get; set; } = [];

        /// <summary>The named selectors that matched no element.</summary>
        public string[] Missing { get; set; } = [];
    }
}
