import { expect } from '@playwright/test';

/**
 * The four cells every scenario runs in. Both themes, and the two widths the F5 acceptance
 * criteria name: the desktop the dashboard is judged on and the 375 px it must not scroll at.
 */
export const MATRIX = [
  { name: 'light 1400', theme: 'light', width: 1400, height: 900 },
  { name: 'dark 1400', theme: 'dark', width: 1400, height: 900 },
  { name: 'light 375', theme: 'light', width: 375, height: 780 },
  { name: 'dark 375', theme: 'dark', width: 375, height: 780 },
];

export const DESKTOP = MATRIX[0];

/**
 * Collects everything the page said that it should not have. A caught exception that blanks a
 * screen is how the Access page broke once, so the console is an assertion rather than a log.
 */
export function guardConsole(page) {
  const noise = [];
  page.on('console', (message) => {
    if (message.type() === 'error' || message.type() === 'warning') {
      noise.push(`console.${message.type()}: ${message.text()}`);
    }
  });
  page.on('pageerror', (error) => noise.push(`pageerror: ${error.message}`));
  page.on('requestfailed', (request) => {
    // The Google Fonts stylesheet is the one request that may legitimately fail offline.
    if (!request.url().includes('fonts.g')) {
      noise.push(`requestfailed: ${request.url()} ${request.failure()?.errorText ?? ''}`);
    }
  });
  return {
    get entries() {
      return noise;
    },
    assertClean() {
      expect(noise, `the page logged ${noise.length} thing(s) it should not have`).toEqual([]);
    },
  };
}

/** Opens a route in one matrix cell and waits for the shell to have rendered something. */
export async function open(page, route, cell = DESKTOP) {
  await page.setViewportSize({ width: cell.width, height: cell.height });
  await page.goto(`index.html${route}`);
  await page.evaluate((theme) => {
    document.documentElement.setAttribute('data-theme', theme);
  }, cell.theme);
  await expect(page.locator('#app')).not.toBeEmpty();
  return page;
}

/** Navigates within the already-loaded app, which is hash-routed and re-renders synchronously. */
export async function go(page, route) {
  await page.evaluate((r) => {
    window.location.hash = r;
  }, route);
  await page.waitForTimeout(60);
}

/** Fails when the document scrolls sideways — the 375 px acceptance criterion, measured. */
export async function expectNoHorizontalScroll(page) {
  const overflow = await page.evaluate(() => {
    const d = document.documentElement;
    return d.scrollWidth - d.clientWidth;
  });
  expect(overflow, 'the document scrolls horizontally').toBeLessThanOrEqual(1);
}

/** Fails when any visible element spills past the viewport's right edge. */
export async function expectNothingClipped(page) {
  const spills = await page.evaluate(() => {
    const width = document.documentElement.clientWidth;
    const out = [];
    /* An element inside a scroll container is not clipped — it is scrollable, which is a
       different thing and a legitimate one for a grid or a code pane. */
    const inScroller = (el) => {
      for (let node = el.parentElement; node; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowX;
        if (overflow === 'auto' || overflow === 'scroll') return true;
      }
      return false;
    };
    for (const el of document.querySelectorAll('#app *')) {
      const style = getComputedStyle(el);
      if (style.display === 'none' || style.visibility === 'hidden' || style.overflowX !== 'visible') continue;
      const box = el.getBoundingClientRect();
      if (box.width === 0 && box.height === 0) continue;
      if (box.right > width + 1 && !inScroller(el)) {
        out.push(`${el.tagName.toLowerCase()}.${el.className}`.slice(0, 120));
      }
    }
    return out.slice(0, 5);
  });
  expect(spills, 'elements spill past the viewport').toEqual([]);
}

/** Fails when two elements that should sit side by side actually cover one another. */
export async function expectNoOverlap(page, a, b) {
  const one = await page.locator(a).boundingBox();
  const two = await page.locator(b).boundingBox();
  expect(one, `${a} is not on screen`).not.toBeNull();
  expect(two, `${b} is not on screen`).not.toBeNull();
  const intersects =
    one.x < two.x + two.width && two.x < one.x + one.width &&
    one.y < two.y + two.height && two.y < one.y + one.height;
  expect(intersects, `${a} overlaps ${b}`).toBe(false);
}

/** The placeholders a design prototype must never show a reader. */
const PLACEHOLDERS = [/\bLorem ipsum\b/i, /\bTODO\b/, /\bTBD\b/, /\bundefined\b/, /\bNaN\b/, /\[object Object\]/];

export async function expectNoVisiblePlaceholder(page) {
  const text = await page.locator('#app').innerText();
  for (const pattern of PLACEHOLDERS) {
    expect(text, `a placeholder matching ${pattern} is visible`).not.toMatch(pattern);
  }
}

/** Saves a screenshot under tests/screenshots/ with a stable name. */
export async function shot(page, name) {
  await page.screenshot({ path: new URL(`./screenshots/${name}.png`, import.meta.url).pathname, fullPage: false });
}

/** Every control on the page that is neither wired nor visibly inert. */
export async function deadControls(page) {
  return page.evaluate(() => {
    const out = [];
    const candidates = document.querySelectorAll('#app button, #app [data-act], #app [role="switch"], #app [role="checkbox"]');
    for (const el of candidates) {
      if (el.hasAttribute('disabled') || el.getAttribute('aria-disabled') === 'true') continue;
      if (el.closest('a[href]')) continue;
      const act = el.getAttribute('data-act') ?? el.closest('[data-act]')?.getAttribute('data-act');
      if (!act && el.tagName === 'BUTTON' && !el.id && !el.getAttribute('type')) {
        out.push(el.textContent.trim().slice(0, 60));
      }
    }
    return out;
  });
}

/** Puts the prototype back to the field-service example at revision 7. */
export async function reset(page) {
  await page.evaluate(() => window.__alvoPrototype.reset());
}

/** The shell's one unapplied count. */
export async function unapplied(page) {
  return page.evaluate(() => window.__alvoPrototype.count());
}

/** Every change the working copy holds, with its kind. */
export async function changeList(page) {
  return page.evaluate(() => window.__alvoPrototype.changes().map((c) => ({ kind: c.kind, pointer: c.pointer, label: c.label })));
}

/** The applied revision number. */
export async function revision(page) {
  return page.evaluate(() => window.__alvoPrototype.wc.revision);
}

/** The working copy, as JSON. */
export async function working(page) {
  return page.evaluate(() => JSON.parse(JSON.stringify(window.__alvoPrototype.wc.working)));
}

/** Counts the moves a scenario costs, so "how many clicks" is measured rather than felt. */
export function moves(page) {
  let n = 0;
  const wrapped = {
    async click(selector, options) { n += 1; await page.click(selector, options); },
    async fill(selector, value) { n += 1; await page.fill(selector, value); },
    async press(selector, key) { n += 1; await page.press(selector, key); },
    get count() { return n; },
  };
  return wrapped;
}
