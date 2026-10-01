/* Acceptance criterion §6.3-5 — WCAG AA contrast, "an automated check over the tokens, not a
   manual pass".

   It is the criterion most easily satisfied by assertion, so it is the one worth measuring. D6
   exists because somebody measured `#128a52` on white and got 4.39 : 1 — below AA for the 12.5 px
   the drawn primary buttons set. Nothing catches the next one of those except this. */

import { test, expect } from '@playwright/test';
import { open } from './helpers.js';

/** Relative luminance, WCAG 2.1 §relative luminance. */
function luminance([r, g, b]) {
  const channel = (v) => {
    const c = v / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function ratio(a, b) {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const parse = (value) => value.match(/\d+(\.\d+)?/g).slice(0, 3).map(Number);

/* Every pair that actually renders, with the smallest size it renders at. AA is 4.5 : 1 for text
   under 18.66 px (or under 14 px bold), which is every pair below. */
const PAIRS = [
  ['--text', '--bg'],
  ['--text', '--panel'],
  ['--text', '--panel2'],
  ['--text', '--codeBg'],
  ['--dim', '--bg'],
  ['--dim', '--panel'],
  ['--dim', '--panel2'],
  ['--faint', '--bg'],
  ['--faint', '--panel'],
  ['--accent', '--bg'],
  ['--accent', '--panel'],
  ['--accentText', '--accent'],
  ['--ok-fg', '--ok-soft'],
  ['--warn-fg', '--warn-soft'],
  ['--danger-fg', '--danger-soft'],
];

/* The measurement itself, measured. A contrast test whose ratio function is broken passes
   everything, which is the vacuous-green failure mode this repository has already met once. The
   two anchors are the ones D6 was decided on. */
test('the ratio function is not vacuous', () => {
  expect(ratio([18, 138, 82], [255, 255, 255])).toBeCloseTo(4.39, 1);   // the drawing's #128a52 — refused
  expect(ratio([15, 122, 72], [255, 255, 255])).toBeCloseTo(5.39, 1);   // D6's #0f7a48 — taken
  expect(ratio([0, 0, 0], [255, 255, 255])).toBeCloseTo(21, 0);
  expect(ratio([255, 255, 255], [255, 255, 255])).toBeCloseTo(1, 2);
});

for (const theme of ['light', 'dark']) {
  test(`every token pair meets WCAG AA — ${theme}`, async ({ page }) => {
    await open(page, '#/overview', { theme, width: 1400, height: 900 });

    const measured = await page.evaluate((pairs) => {
      const style = getComputedStyle(document.documentElement);
      const out = [];
      for (const [fg, bg] of pairs) {
        const one = style.getPropertyValue(fg).trim();
        const two = style.getPropertyValue(bg).trim();
        if (!one || !two) continue;
        // Resolve through a probe element so light-dark() and color-mix() are computed.
        const probe = document.createElement('span');
        probe.style.color = `var(${fg})`;
        probe.style.backgroundColor = `var(${bg})`;
        document.body.append(probe);
        const computed = getComputedStyle(probe);
        out.push({ fg, bg, color: computed.color, background: computed.backgroundColor });
        probe.remove();
      }
      return out;
    }, PAIRS);

    expect(measured.length, 'no token pair resolved — the stylesheet did not load').toBeGreaterThan(8);

    const failures = [];
    for (const { fg, bg, color, background } of measured) {
      const value = ratio(parse(color), parse(background));
      if (value < 4.5) failures.push(`${fg} on ${bg} is ${value.toFixed(2)} : 1`);
    }
    expect(failures, 'WCAG AA requires 4.5 : 1 for normal text').toEqual([]);
  });
}

test('the accent is D6’s value, not the drawing’s', async ({ page }) => {
  await open(page, '#/overview', { theme: 'light', width: 1400, height: 900 });
  const accent = await page.evaluate(() => {
    const probe = document.createElement('span');
    probe.style.color = 'var(--accent)';
    document.body.append(probe);
    const value = getComputedStyle(probe).color;
    probe.remove();
    return value;
  });
  // #0f7a48 = rgb(15, 122, 72). The drawing's #128a52 measured 4.39 : 1 on white and is refused.
  expect(parse(accent)).toEqual([15, 122, 72]);
});
