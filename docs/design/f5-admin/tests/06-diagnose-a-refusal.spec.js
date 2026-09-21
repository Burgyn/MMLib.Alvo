/* Scenario 6 — Diagnose a refusal.

   "Why can Peter not see WO-100419?" Get to the answer, and check the answer is true.

   The true answer has three parts, and the old prototype got the third one wrong:
     1. Peter holds `technician`, and the `get` rule on work_orders admits a technician only
        through `assigned_to == @user.id`.
     2. WO-100419's `assigned_to` names Martin.
     3. Therefore he is excluded by a row-level predicate — which on `get` is a **404**, and on
        `list` is simply a shorter page. It is NOT a 403, and the dashboard must not say it is.

   And the route to it must not pass through a client that scored the row, because that is the
   second policy evaluator §6.3-4 forbids. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, shot, moves, expectNoHorizontalScroll, expectNothingClipped , openPerson } from './helpers.js';

const PETER = 'c3f6e990-73b4-4d1c-a052-8e41bd3f6c27';
const MARTIN = 'b2e5d88f-62a3-4c0b-9f41-7d30ac2e5b16';

test.describe('diagnose a refusal', () => {
  test('works: the answer is reachable, and it is the true one', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');
    const m = moves(page);

    // 1. Who is Peter, and what does the rule admit him through?
    await openPerson(page, PETER);
    const ladder = page.locator('.a-ladder');
    await expect(ladder).toContainText('technician');
    await expect(ladder).toContainText('only the rows assigned_to names them in');
    await shot(page, '06-ladder');

    // 2. What does WO-100419 say?
    await go(page, '#/data/work_orders');
    await m.click('[data-kind="record"][data-id="wo_2c09d"]:visible');
    await page.waitForTimeout(80);
    await expect(page.locator('.a-drawer')).toContainText('WO-100419');
    await expect(page.locator('.a-drawer')).toContainText('martin@field-service.sk');
    await shot(page, '06-record');

    // The data really is what the answer rests on.
    const row = await page.evaluate(() => window.__alvoPrototype.state && (
      [...document.querySelectorAll('.p-kv dt')].map((dt) => [dt.textContent, dt.nextElementSibling.textContent.trim()])
    ));
    const assigned = row.find(([k]) => k === 'assigned_to');
    expect(assigned[1]).toContain('martin@field-service.sk');

    // 3. And the shape of the refusal, stated where the question is asked.
    await go(page, '#/rules/work_orders');
    await page.click(`[data-act="sim"][data-k="user"][data-v="${PETER}"]`);
    await page.click('[data-act="sim"][data-k="operation"][data-v="get"]');
    await page.waitForTimeout(80);

    // It is NOT a 403 — the engine resolved a policy. The row-level exclusion is a 404.
    await expect(page.locator('[data-verdict]')).toHaveAttribute('data-verdict', 'resolved');
    await expect(page.locator('#app')).toContainText('404');
    await expect(page.locator('#app')).toContainText('invisible to me');
    await expect(page.locator('#app')).toContainText('assigned_to == @user.id');

    // And on a list it is a shorter page rather than an error — the RLS surprise, stated.
    await page.click('[data-act="sim"][data-k="operation"][data-v="list"]');
    await page.waitForTimeout(80);
    await expect(page.locator('#app')).toContainText('a shorter list, not an error');
    await expect(page.locator('#app')).toContainText('200');
    await shot(page, '06-verdict-list');

    // usable: four moves from the question to the answer.
    expect(m.count, 'the diagnosis should stay under six moves').toBeLessThanOrEqual(6);
    console_.assertClean();
  });

  test('works: nothing anywhere scores a stored record', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/work_orders');
    await reset(page);

    for (const entity of ['work_orders', 'customers', 'regions']) {
      await go(page, `#/rules/${entity}`);
      for (const who of [PETER, MARTIN, '']) {
        await page.click(`[data-act="sim"][data-k="user"][data-v="${who}"]`);
        await page.waitForTimeout(40);
        const text = await page.locator('[data-simulator]').innerText();
        // No per-record verdict, and no record picker to produce one.
        expect(text, 'the simulator must not name a record').not.toMatch(/WO-\d+/);
        expect(await page.locator('[data-simulator] select').count()).toBe(0);
        expect(text).not.toMatch(/this record's/i);
      }
    }

    // And the panel says why, in the API's own terms.
    await expect(page.locator('[data-simulator]')).toContainText('takes no record id');
    await expect(page.locator('[data-simulator]')).toContainText('second policy evaluator');

    console_.assertClean();
  });

  test('works: a caller who matches no level is told, and the three predicates are named', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');

    // field-service declares no `access` block at all, so only the bootstrap admin gets in.
    await openPerson(page, MARTIN);
    const ladder = page.locator('.a-ladder');
    await expect(ladder).toContainText('Cannot open it');
    await expect(ladder).toContainText('The three predicates are');
    await expect(ladder).toContainText('not declared');

    // The Access screen says the same thing about the empty block, rather than leaving it silent.
    await page.click('[data-act="close"]');
    await page.waitForTimeout(60);
    await expect(page.locator('#app')).toContainText('This descriptor declares no level at all');
    await expect(page.locator('#app')).toContainText('nobody but the deployment');

    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/access', cell);
      await reset(page);
      await go(page, '#/access');
      await openPerson(page, PETER);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      console_.assertClean();
    });
  }
});
