/* Scenario 3 — Change a model safely, then unsafely.

   Widen a decimal. Then change a field's type on an entity that has rows behind it, and confirm
   the destructive path asks for the entity name and says what is lost.

   What it is really checking: that `allowDestructive` is never implied — not by having previewed,
   and not by the caller's management level — and that the warning about a type change appears when
   the type CHANGES, rather than permanently on every field that happens to carry a facet. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, unapplied, revision, working, shot, expectNoHorizontalScroll, expectNothingClipped } from './helpers.js';

test.describe('safe, then unsafe', () => {
  test('works: widening a decimal is safe and applies without a confirmation', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/work_orders');
    await reset(page);
    await go(page, '#/schema/work_orders');

    await page.click('[data-act="field"][data-field="quoted_price"]');
    // No type-change warning on an untouched field: the old version showed it permanently.
    await expect(page.locator('[data-typewarn]')).toHaveCount(0);

    await page.fill('[data-key="precision"]', '12');
    await page.waitForTimeout(60);

    expect(await unapplied(page)).toBe(1);
    const doc = await working(page);
    expect(doc.entities.work_orders.fields.quoted_price).toMatchObject({ precision: 12, scale: 2 });

    // The pane shows the working copy — the number 12 is really in it.
    await expect(page.locator('[data-pane-header]')).toContainText('working copy · 1 change vs r7');
    await expect(page.locator('[data-descriptor]')).toContainText('12');
    await expect(page.locator('.a-json__hit--changed')).toHaveCount(1);

    await go(page, '#/schema/preview');
    await expect(page.locator('#app')).toContainText('Widen work_orders.quoted_price precision from 10 to 12');
    await expect(page.locator('[data-destructive]')).toHaveCount(0);
    await expect(page.locator('[data-destructive-gate]')).toHaveCount(0);
    await expect(page.locator('[data-act="apply"]')).toBeEnabled();
    await shot(page, '03-safe-widen');

    await page.fill('#apply-reason', 'Larger quotes than decimal(10,2) fits');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(120);
    expect(await revision(page)).toBe(8);

    console_.assertClean();
  });

  test('works: a type change over rows asks for the entity name and says what is lost', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/work_orders');
    await reset(page);
    await go(page, '#/schema/work_orders');

    await page.click('[data-act="field"][data-field="is_emergency"]');
    await page.click('[data-act="settype"][data-type="enum"]');
    await page.waitForTimeout(60);

    // The warning fires now — on a change, at the control that caused it, naming the row count.
    await expect(page.locator('[data-typewarn]')).toBeVisible();
    await expect(page.locator('[data-typewarn]')).toContainText('Changed from');
    await expect(page.locator('[data-typewarn]')).toContainText('boolean');
    await expect(page.locator('[data-typewarn]')).toContainText('24,680 rows');
    await expect(page.locator('[data-typewarn]')).toContainText('type work_orders');

    await go(page, '#/schema/preview');

    // The plan marks the step destructive and says what it loses.
    await expect(page.locator('[data-step][data-destructive]')).toHaveCount(1);
    await expect(page.locator('[data-step][data-destructive]')).toContainText('Change work_orders.is_emergency from boolean to enum');
    await expect(page.locator('[data-step][data-destructive]')).toContainText('Loses every value in work_orders.is_emergency that the new type cannot hold');

    // The gate exists, asks for the entity name, and Apply is not pre-authorised by the preview.
    const gate = page.locator('[data-destructive-gate]');
    await expect(gate).toBeVisible();
    await expect(gate).toContainText('allowDestructive');
    await expect(gate).toContainText('never implied');
    await expect(gate.locator('[data-act="confirmword"]')).toHaveAttribute('data-word', 'work_orders');
    await shot(page, '03-destructive-gate');

    // Applying without typing it is refused, with the API's own slug.
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(80);
    await expect(page.locator('[data-refused-destructive]')).toBeVisible();
    await expect(page.locator('[data-refused-destructive]')).toContainText('destructive-change');
    expect(await revision(page)).toBe(7);
    expect(await unapplied(page)).toBeGreaterThan(0);

    // Typing the wrong name does not unlock it either.
    await page.fill('[data-act="confirmword"]', 'work_order');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(80);
    expect(await revision(page)).toBe(7);

    // Typing it does.
    await page.fill('[data-act="confirmword"]', 'work_orders');
    await page.fill('#apply-reason', 'Emergency becomes a three-value enum');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(140);
    expect(await revision(page)).toBe(8);
    expect(await unapplied(page)).toBe(0);

    console_.assertClean();
  });

  test('works: dropping a field is destructive too, and names what it loses', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/work_orders');
    await reset(page);
    await go(page, '#/schema/work_orders');

    await page.click('[data-act="field"][data-field="external_ref"]');
    await page.click('[data-act="removefield"]');
    await go(page, '#/schema/preview');

    await expect(page.locator('[data-step][data-destructive]')).toContainText('Drop the column work_orders.external_ref');
    await expect(page.locator('[data-step][data-destructive]')).toContainText('Loses every value stored in work_orders.external_ref');
    await expect(page.locator('[data-destructive-gate]')).toBeVisible();

    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/schema/work_orders', cell);
      await reset(page);
      await go(page, '#/schema/work_orders');
      await page.click('[data-act="field"][data-field="is_emergency"]');
      await page.click('[data-act="settype"][data-type="enum"]');
      await go(page, '#/schema/preview');
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      await expect(page.locator('[data-destructive-gate]')).toBeVisible();
      console_.assertClean();
    });
  }
});
