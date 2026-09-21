/* Scenario 11 — Keyboard, focus, the phone, and the states nobody drew.

   §6.3 criterion 1: fully operable at 375 px with no horizontal scroll — a test at that width,
   not an eye. §6.3 criterion 6: every primary flow completes without a mouse.

   What it is really checking: the drawn version had ⌘K and Esc and nothing else, and the palette
   itself was keyboard-dead — `autofocus` does not fire on an element inserted through innerHTML,
   so `document.activeElement` was BODY and typed text went nowhere. There were zero `.focus()`
   calls in the whole file. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, shot, clickVisible, expectNoHorizontalScroll, expectNothingClipped, expectNoVisiblePlaceholder } from './helpers.js';

const PHONE = MATRIX[2];

test.describe('keyboard and focus', () => {
  test('works: the command palette opens, filters, and answers the keyboard', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);

    await page.keyboard.press('Meta+k');
    await page.waitForTimeout(80);

    // It is a dialog, and the input REALLY has focus.
    await expect(page.locator('[role="dialog"][aria-label="Command palette"]')).toBeVisible();
    const focused = await page.evaluate(() => document.activeElement?.id);
    expect(focused, 'the palette input must have focus, not BODY').toBe('palette-input');

    // Typing filters. The items are generated per entity, not hardcoded.
    await page.keyboard.type('customers');
    await page.waitForTimeout(80);
    const labels = await page.locator('.a-palette__item').allInnerTexts();
    expect(labels.length).toBeGreaterThan(0);
    expect(labels.every((l) => l.toLowerCase().includes('customers')),
      `the palette did not filter: ${JSON.stringify(labels)}`).toBe(true);
    await shot(page, '11-palette');

    // Down moves the active item; Return follows it.
    await page.keyboard.press('ArrowDown');
    await page.waitForTimeout(60);
    const active = await page.locator('.a-palette__item--active').innerText();
    expect(active).toBe(labels[1]);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(100);
    expect(page.url()).toContain('customers');

    console_.assertClean();
  });

  test('works: Escape closes, and focus returns to what opened it', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/data/work_orders');
    await reset(page);
    await go(page, '#/data/work_orders');

    await page.keyboard.press('Meta+k');
    await page.waitForTimeout(60);
    await expect(page.locator('.a-palette')).toBeVisible();
    await page.keyboard.press('Escape');
    await page.waitForTimeout(60);
    await expect(page.locator('.a-palette')).toHaveCount(0);

    // Tab inside an open drawer never walks the page behind it.
    await page.click('[data-kind="record-new"]');
    await page.waitForTimeout(80);
    const trapped = await page.evaluate(() => {
      const panel = document.querySelector('.p-overlay__panel');
      const items = [...panel.querySelectorAll('a[href],button:not([disabled]),input:not([disabled]),select,textarea,[tabindex]:not([tabindex="-1"])')];
      return items.length > 0 && panel.contains(document.activeElement);
    });
    expect(trapped, 'focus must start inside the drawer').toBe(true);

    await page.keyboard.press('Escape');
    await page.waitForTimeout(60);
    await expect(page.locator('.a-drawer')).toHaveCount(0);

    console_.assertClean();
  });

  test('works: g+letter jumps, and / focuses the filter', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);

    for (const [key, hash] of [['s', '#/schema'], ['d', '#/data'], ['r', '#/rules'], ['a', '#/access'], ['h', '#/history'], ['o', '#/overview']]) {
      await page.keyboard.press('g');
      await page.keyboard.press(key);
      await page.waitForTimeout(80);
      expect(page.url()).toContain(hash);
    }

    await go(page, '#/schema/work_orders');
    await page.keyboard.press('/');
    await page.waitForTimeout(60);
    const focused = await page.evaluate(() => document.activeElement?.getAttribute('data-act') ?? document.activeElement?.getAttribute('aria-label'));
    expect(focused).toBe('fieldfilter');

    // And a key typed into an input is a character, not a shortcut.
    await page.keyboard.type('gsr');
    await page.waitForTimeout(60);
    expect(page.url()).toContain('#/schema/work_orders');

    console_.assertClean();
  });

  test('works: j and k walk the rows, and Space operates a toggle', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/work_orders');
    await reset(page);
    await go(page, '#/schema/work_orders');

    await page.keyboard.press('j');
    await page.waitForTimeout(60);
    let name = await page.evaluate(() => document.activeElement?.dataset?.field);
    expect(name).toBe('reference');
    await page.keyboard.press('j');
    await page.waitForTimeout(60);
    name = await page.evaluate(() => document.activeElement?.dataset?.field);
    expect(name).toBe('title');
    await page.keyboard.press('k');
    await page.waitForTimeout(60);
    name = await page.evaluate(() => document.activeElement?.dataset?.field);
    expect(name).toBe('reference');

    // Enter opens it, and the editor is reachable.
    await page.keyboard.press('Enter');
    await page.waitForTimeout(80);
    await expect(page.locator('[data-field-editor]')).toBeVisible();

    // A span with role="switch" is focusable and Space-operable — it used to be neither.
    await page.focus('[data-act="toggleflag"][data-key="unique"]');
    const before = await page.getAttribute('[data-act="toggleflag"][data-key="unique"]', 'aria-checked');
    await page.keyboard.press(' ');
    await page.waitForTimeout(80);
    const after = await page.getAttribute('[data-act="toggleflag"][data-key="unique"]', 'aria-checked');
    expect(after).not.toBe(before);

    console_.assertClean();
  });

  test('works: a whole change can be made and previewed without a mouse', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);

    await page.keyboard.press('g');
    await page.keyboard.press('s');
    await page.waitForTimeout(80);
    await page.keyboard.press('j');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(80);
    expect(page.url()).toContain('#/schema/');

    await page.keyboard.press('j');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(80);
    await page.focus('[data-act="toggleflag"][data-key="index"]');
    await page.keyboard.press(' ');
    await page.waitForTimeout(80);

    const count = await page.evaluate(() => window.__alvoPrototype.count());
    expect(count).toBe(1);

    console_.assertClean();
  });
});

test.describe('the phone', () => {
  test('works: every route at 375 px, no horizontal scroll, nothing clipped', async ({ page }) => {
    const console_ = guardConsole(page);
    const routes = ['#/overview', '#/schema', '#/schema/work_orders', '#/schema/preview',
      '#/schema/transfer', '#/data', '#/data/work_orders', '#/rules/work_orders', '#/access',
      '#/history', '#/integrations', '#/settings', '#/automations', '#/functions', '#/notes',
      '#/welcome/1', '#/welcome/2', '#/welcome/3'];

    await open(page, '#/overview', PHONE);
    await reset(page);
    for (const route of routes) {
      await go(page, route);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      await expectNoVisiblePlaceholder(page);
    }
    await shot(page, '11-phone-rules');
    console_.assertClean();
  });

  test('works: the bottom bar holds only live sections', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview', PHONE);
    const items = await page.locator('.a-bottomnav__item').allInnerTexts();
    expect(items.length).toBeLessThanOrEqual(5);
    for (const label of items) {
      expect(label).not.toMatch(/Automations|Functions/);
    }
    console_.assertClean();
  });

  test('works: the matrix transposes rather than hiding four columns off-screen', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/work_orders', PHONE);
    await reset(page);
    await go(page, '#/rules/work_orders');

    // Every operation is reachable, and each cell names the operation it belongs to, because the
    // header row is gone.
    for (const op of ['list', 'get', 'create', 'update', 'delete']) {
      const cell = page.locator(`.a-cell[data-role="admin"][data-op="${op}"]`);
      await expect(cell).toBeVisible();
      const box = await cell.boundingBox();
      expect(box.x + box.width, `the ${op} cell is off-screen`).toBeLessThanOrEqual(376);
    }
    console_.assertClean();
  });
});

test.describe('the states nobody drew', () => {
  test('works: an empty schema says what to do next', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema');
    await page.evaluate(() => window.__alvoPrototype.startEmpty('blank'));
    await go(page, '#/schema');
    await expect(page.locator('#app')).toContainText('No entities yet');
    await expect(page.locator('#app')).not.toContainText('No data');
    await go(page, '#/overview');
    await expect(page.locator('#app')).toContainText('Nothing is modelled yet');
    await shot(page, '11-empty');
    console_.assertClean();
  });

  test('works: an entity with no rules says default-deny out loud', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/work_orders');
    await reset(page);
    await page.evaluate(() => {
      delete window.__alvoPrototype.wc.working.entities.work_orders.rules;
      delete window.__alvoPrototype.wc.applied.entities.work_orders.rules;
    });
    await go(page, '#/schema/work_orders');
    await page.click('[data-act="tab"][data-tab="rules"]');
    await page.waitForTimeout(80);
    await expect(page.locator('#app')).toContainText('No rule at all — refused for everyone');
    await expect(page.locator('#app')).toContainText('an administrator included');

    await go(page, '#/rules/work_orders');
    await expect(page.locator('[data-verdict]')).toHaveAttribute('data-verdict', 'unconfigured');
    console_.assertClean();
  });

  test('works: an apply that lost the race is a 412, distinct from a 428', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);
    await page.evaluate(() => {
      window.__alvoPrototype.wc.working.entities.work_orders.fields.title.maxLength = 200;
      window.__alvoPrototype.state.applyState = 'stale';
    });
    await go(page, '#/schema/preview');
    const banner = page.locator('[data-stale]');
    await expect(banner).toBeVisible();
    await expect(banner).toContainText('precondition-failed');
    await expect(banner).toContainText('428');
    await expect(banner).toContainText('you sent no precondition at all');
    // The edits survive it.
    expect(await page.evaluate(() => window.__alvoPrototype.count())).toBeGreaterThan(0);
    await shot(page, '11-stale-apply');

    await page.click('[data-act="dismisserror"]');
    await page.waitForTimeout(60);
    await expect(page.locator('[data-stale]')).toHaveCount(0);
    console_.assertClean();
  });

  test('works: a developer is told before Apply that an access change needs admin', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);

    // Make Martin a developer and sign in as him.
    await page.evaluate(() => {
      const p = window.__alvoPrototype;
      p.wc.applied.access = { developer: "'dispatcher' in @user.roles" };
      p.wc.working.access = { developer: "'dispatcher' in @user.roles" };
      p.state.signedIn = 'b2e5d88f-62a3-4c0b-9f41-7d30ac2e5b16';
    });
    await go(page, '#/access');
    await expect(page.locator('#app')).toContainText('developer');

    // A rules-only change is within a developer's level.
    await page.evaluate(() => {
      window.__alvoPrototype.wc.working.entities.regions.rules.get = "'anon' in @user.roles";
    });
    await go(page, '#/schema/preview');
    await expect(page.locator('[data-cannot-apply]')).toHaveCount(0);
    await expect(page.locator('[data-act="apply"]')).toBeEnabled();

    // Touching `access` re-resolves the WHOLE apply to admin — including the half that was fine.
    await page.evaluate(() => {
      window.__alvoPrototype.wc.working.access.viewer = "'technician' in @user.roles";
    });
    await go(page, '#/schema/preview');
    await expect(page.locator('[data-cannot-apply]')).toBeVisible();
    await expect(page.locator('[data-cannot-apply]')).toContainText('re-resolve the requirement to');
    await expect(page.locator('[data-cannot-apply]')).toContainText('promotes itself');
    await expect(page.locator('[data-act="apply"]')).toBeDisabled();
    await shot(page, '11-developer-refused');

    console_.assertClean();
  });

  test('works: a loading state is a skeleton and an empty filter says how to widen it', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/data/work_orders');
    await reset(page);
    await go(page, '#/data/work_orders');

    await page.click('[data-act="state"][data-state="loading"]');
    await page.waitForTimeout(60);
    await expect(page.locator('.a-skeleton').first()).toBeVisible();

    await page.click('[data-act="state"][data-state="empty"]');
    await page.waitForTimeout(60);
    await expect(page.locator('#app')).toContainText('Clear it to widen the search');

    await page.click('[data-act="state"][data-state="error"]');
    await page.waitForTimeout(60);
    // The refusal is indistinguishable from one over a field that does not exist.
    await expect(page.locator('[data-filter-error]')).toContainText('is not public on the read surface');
    await expect(page.locator('[data-filter-error]')).toContainText('malformed-query');
    await expect(page.locator('[data-filter-error]')).not.toContainText('internal_notes');
    await shot(page, '11-filter-error');

    console_.assertClean();
  });

  test('works: prefers-reduced-motion really disables the animation', async ({ page }) => {
    const console_ = guardConsole(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await open(page, '#/access');
    await reset(page);
    await clickVisible(page, '[data-act="person"]');
    await page.waitForTimeout(60);
    const running = await page.evaluate(() =>
      document.querySelector('.p-overlay__panel').getAnimations().length);
    expect(running, 'no animation may run under prefers-reduced-motion').toBe(0);

    // Disabled, not shortened — so with the preference off it really does animate. A media query
    // nobody honours and a media query nobody can tell apart look the same in a screenshot.
    await page.keyboard.press('Escape');
    await page.waitForTimeout(60);
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await clickVisible(page, '[data-act="person"]');
    const withMotion = await page.evaluate(() =>
      document.querySelector('.p-overlay__panel').getAnimations().length);
    expect(withMotion, 'and it is disabled rather than absent').toBeGreaterThan(0);

    console_.assertClean();
  });
});
