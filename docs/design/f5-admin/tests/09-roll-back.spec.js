/* Scenario 9 — Roll back.

   Compare two revisions. Restore an older one with the typed confirmation.

   What it is really checking: a restore does not rewind the history — it appends a NEW revision
   carrying the old descriptor, with `rolledBackFrom` naming what it restored. The reverse
   migration routinely drops what the forward one added, which is the case the destructive
   guardrail exists for. And any two revisions compare, not only against the head. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, revision, working, shot, moves, expectNoHorizontalScroll, expectNothingClipped } from './helpers.js';

test.describe('roll back', () => {
  test('works: any two revisions compare, not only against the head', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/history');
    await reset(page);
    await go(page, '#/history');

    // The default pair is the last two.
    await expect(page.locator('#app')).toContainText('Comparing r6 with r7');

    // r3 against r4 — the pair the old screen could not produce at all.
    await page.click('[data-act="compare"][data-side="a"][data-rev="3"]');
    await page.click('[data-act="compare"][data-side="b"][data-rev="4"]');
    await page.waitForTimeout(80);
    await expect(page.locator('#app')).toContainText('r3 → r4');
    await expect(page.locator('.a-diff').first()).toBeVisible();
    // r4 is the revision that modelled the emergency flag as an enum.
    await expect(page.locator('.a-split__aside')).toContainText('is_emergency');
    await shot(page, '09-compare-r3-r4');

    // `rolledBackFrom` is printed as what it means — the revision this one RESTORED — and not
    // as an arithmetic guess. r5 restored r3.
    await expect(page.locator('[data-revision="5"]')).toContainText('restored r3');

    console_.assertClean();
  });

  test('works: a restore appends a revision, and asks for the name when it destroys', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/history');
    await reset(page);
    await go(page, '#/history');
    const m = moves(page);

    // r6 is r7 without `access_code`, so restoring it DROPS a column — the case the guardrail is
    // for, and the one a reverse migration hits routinely.
    await m.click('[data-kind="rollback"][data-id="6"]');
    await page.waitForTimeout(80);

    const modal = page.locator('.a-modal');
    await expect(modal).toContainText('does not rewind the history');
    await expect(modal).toContainText('rolledBackFrom = 6');
    await expect(modal).toContainText('The reverse migration discards data');
    await expect(modal).toContainText('work_orders.access_code');
    await expect(modal).toContainText('never implied by the route');
    await shot(page, '09-rollback-confirm');

    // The button is disabled until the name is typed.
    const confirm = page.locator('[data-act="dorollback"]');
    await expect(confirm).toBeDisabled();
    await m.fill('[data-act="confirmword"]', 'wrong-name');
    await expect(confirm).toBeDisabled();
    await m.fill('[data-act="confirmword"]', 'field-service');
    await expect(confirm).toBeEnabled();

    await m.click('[data-act="dorollback"]');
    await page.waitForTimeout(140);

    // A NEW revision, not a rewind: r7 is still in the history and r8 points back at r6.
    expect(await revision(page)).toBe(8);
    await expect(page.locator('[data-revision="7"]')).toBeVisible();
    await expect(page.locator('[data-revision="8"]')).toContainText('Rollback to revision 6');
    await expect(page.locator('[data-revision="8"]')).toContainText('restored r6');
    await expect(page.locator('[data-revision="8"]')).toContainText('jana@field-service.sk');

    // And the descriptor really moved: access_code is gone.
    const doc = await working(page);
    expect(doc.entities.work_orders.fields.access_code).toBeUndefined();
    await shot(page, '09-rolled-back');

    console_.assertClean();
  });

  test('works: a restore that destroys nothing asks for no name', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/history');
    await reset(page);

    // Apply a rules-only change first, so the revision below it is reachable without a drop.
    await page.evaluate(() => {
      window.__alvoPrototype.wc.working.entities.regions.rules.list = "'anon' in @user.roles";
    });
    await go(page, '#/schema/preview');
    await page.fill('#apply-reason', 'Publish regions');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(140);

    await go(page, '#/history');
    await page.click('[data-kind="rollback"][data-id="7"]');
    await page.waitForTimeout(80);

    await expect(page.locator('.a-modal')).toContainText('Nothing stored is lost by this restore');
    await expect(page.locator('[data-act="dorollback"]')).toBeEnabled();
    await page.click('[data-act="dorollback"]');
    await page.waitForTimeout(140);
    expect(await revision(page)).toBe(9);

    const doc = await working(page);
    expect(doc.entities.regions.rules.list).toBe("'authenticated' in @user.roles");

    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/history', cell);
      await reset(page);
      await go(page, '#/history');
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      await page.click('[data-kind="rollback"][data-id="6"]');
      await page.waitForTimeout(80);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      console_.assertClean();
    });
  }
});
