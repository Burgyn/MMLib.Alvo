using Microsoft.Playwright;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// Reads, as the browser laid it out, how every visible text input under a root is drawn: its text size, its box,
/// where its name sits, and what its hint is linked to (spec §3.8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The box is the element that draws the border.</b> For a native input that is the input; for the library's it is
/// the <c>.mud-input</c> frame around the native element, which is where alvo.css draws the border. A select is read
/// by its visible slot, the element that holds the chosen value.
/// </para>
/// <para>
/// <b>The name</b> is the input's <c>label for</c>, or the element its <c>aria-labelledby</c> names. A search box over
/// a list has no visible name by the rule and is read by its <c>aria-label</c> instead.
/// </para>
/// </remarks>
internal static class FieldProbe
{
    private const string Script = """
        (root) => {
          const visible = e => { const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const rect = e => { const r = e.getBoundingClientRect(); return { top: r.top, bottom: r.bottom, left: r.left, right: r.right, height: r.height }; };
          const typed = ['text', 'email', 'password', 'number', 'search', 'url', 'tel', ''];
          const inputs = [...root.querySelectorAll('input, textarea, .mud-select .mud-input-slot')]
            .filter(e => e.tagName !== 'INPUT' || typed.includes((e.getAttribute('type') ?? '').toLowerCase()))
            .filter(e => !(e.tagName === 'INPUT' && e.closest('.mud-select')))
            .filter(visible);
          const labelled = e => (e.getAttribute('aria-labelledby') ?? '').split(/\s+/).filter(Boolean)
            .map(id => document.getElementById(id)).filter(Boolean);
          const namesOf = e => [...(e.labels ?? []), ...labelled(e)];
          const text = n => (n.textContent ?? '').replace(/\s+/g, ' ').trim();
          const fields = inputs.map(e => {
            const box = e.closest('.mud-input') ?? e;
            const b = getComputedStyle(box), own = getComputedStyle(e);
            const names = namesOf(e);
            const label = names[0] ?? null;
            const shown = !!label && visible(label);
            const control = e.closest('.mud-input-control') ?? box;
            const described = (e.getAttribute('aria-describedby') ?? '').split(/\s+/).filter(Boolean);
            const field = e.closest('.a-field');
            const hints = field ? [...field.querySelectorAll(':scope > .a-hint')].filter(visible) : [];
            return {
              Id: e.id || e.tagName.toLowerCase(),
              MultiLine: e.tagName === 'TEXTAREA',
              FontSize: own.fontSize,
              Radius: b.borderTopLeftRadius,
              BorderWidth: b.borderTopWidth,
              BorderStyle: b.borderTopStyle,
              Box: rect(box),
              HasVisibleLabel: shown,
              Label: shown ? rect(label) : null,
              LabelFontSize: shown ? getComputedStyle(label).fontSize : '',
              AriaLabel: e.getAttribute('aria-label') ?? '',
              Name: names.length > 0 ? names.map(text).join(' ') : (e.getAttribute('aria-label') ?? ''),
              ExtraNames: Math.max(0, names.filter(visible).length - 1)
                + [...control.querySelectorAll('.mud-input-label')].filter(visible).length,
              UnlinkedHints: hints.filter(h => !h.id || !described.includes(h.id)).length,
            };
          });
          const checks = [...root.querySelectorAll('.mud-checkbox > .mud-typography, .mud-switch > .mud-typography')]
            .filter(visible).map(e => ({ Text: e.textContent.trim(), FontSize: getComputedStyle(e).fontSize }));
          const tokens = getComputedStyle(document.documentElement);
          const phone = window.matchMedia('(max-width: 720px)').matches;
          return { Fields: fields, Checks: checks,
            TextSize: tokens.getPropertyValue('--text-sm').trim(),
            ValueSize: tokens.getPropertyValue(phone ? '--text-lg' : '--text-sm').trim(),
            Radius: tokens.getPropertyValue('--radius-xs').trim() };
        }
        """;

    /// <summary>Every visible text input under <paramref name="root"/>, and every checkbox or switch label.</summary>
    /// <param name="root">The screen or the dialog, which must be attached and settled.</param>
    /// <returns>The reading.</returns>
    public static async Task<Reading> ReadAsync(ILocator root)
    {
        ArgumentNullException.ThrowIfNull(root);
        await root.Page.WaitForFunctionAsync(
            "() => document.getAnimations().every(a => a.playState !== 'running')").ConfigureAwait(false);
        return await root.EvaluateAsync<Reading>(Script).ConfigureAwait(false);
    }

    /// <summary>What one root holds; settable, because Playwright deserialises into a parameterless type.</summary>
    internal sealed class Reading
    {
        /// <summary>The text inputs, in document order.</summary>
        public Field[] Fields { get; set; } = [];

        /// <summary>The checkbox and switch labels.</summary>
        public Check[] Checks { get; set; } = [];

        /// <summary>The resolved <c>--text-sm</c>, the size a field's name and hint are drawn at.</summary>
        public string TextSize { get; set; } = string.Empty;

        /// <summary>
        /// The size a value is typed at: <c>--text-sm</c>, and <c>--text-lg</c> on a phone, where iOS zooms into a
        /// smaller one on focus.
        /// </summary>
        public string ValueSize { get; set; } = string.Empty;

        /// <summary>The resolved <c>--radius-xs</c>, a field's corner.</summary>
        public string Radius { get; set; } = string.Empty;
    }

    /// <summary>One text input.</summary>
    internal sealed class Field
    {
        /// <summary>Its id, or its tag name when it has none.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Whether it is a textarea.</summary>
        public bool MultiLine { get; set; }

        /// <summary>The size its value is typed at.</summary>
        public string FontSize { get; set; } = string.Empty;

        /// <summary>The box's corner.</summary>
        public string Radius { get; set; } = string.Empty;

        /// <summary>The box's border width.</summary>
        public string BorderWidth { get; set; } = string.Empty;

        /// <summary>The box's border style.</summary>
        public string BorderStyle { get; set; } = string.Empty;

        /// <summary>The box, in viewport pixels.</summary>
        public Rect Box { get; set; } = new();

        /// <summary>Whether its name is drawn.</summary>
        public bool HasVisibleLabel { get; set; }

        /// <summary>The drawn name, when there is one.</summary>
        public Rect? Label { get; set; }

        /// <summary>The size the name is drawn at.</summary>
        public string LabelFontSize { get; set; } = string.Empty;

        /// <summary>Its <c>aria-label</c>, which names a search box.</summary>
        public string AriaLabel { get; set; } = string.Empty;

        /// <summary>Its accessible name's text: its labels, what it is labelled by, or its <c>aria-label</c>.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// How many names beyond one are drawn for it: a second label, or the library's own label inside its control.
        /// </summary>
        public int ExtraNames { get; set; }

        /// <summary>How many hints of its field the input does not point <c>aria-describedby</c> at.</summary>
        public int UnlinkedHints { get; set; }

        /// <inheritdoc />
        public override string ToString() => AriaLabel.Length > 0 ? $"{Id} ({AriaLabel})" : Id;
    }

    /// <summary>A checkbox's or a switch's label.</summary>
    internal sealed class Check
    {
        /// <summary>What it says.</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>The size it is drawn at.</summary>
        public string FontSize { get; set; } = string.Empty;
    }

    /// <summary>A box in viewport pixels.</summary>
    internal sealed class Rect
    {
        /// <summary>Its top edge.</summary>
        public double Top { get; set; }

        /// <summary>Its bottom edge.</summary>
        public double Bottom { get; set; }

        /// <summary>Its left edge.</summary>
        public double Left { get; set; }

        /// <summary>Its right edge.</summary>
        public double Right { get; set; }

        /// <summary>Its height.</summary>
        public double Height { get; set; }
    }
}
