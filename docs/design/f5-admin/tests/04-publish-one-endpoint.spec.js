/* Scenario 4 — Make one endpoint public.

   Tick `anon` on `regions` read. See the warning. Preview. Apply. Then take it back.

   What it is really checking: that an open rule does not look like every other tick. `anon` is
   every caller with no identity at all, so naming it in a rule is how something becomes reachable
   by anybody who can reach the URL — the failure mode the sources single out by name. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, unapplied, changeList, revision, working, shot, moves, expectNoHorizontalScroll, expectNothingClipped } from './helpers.js';

const anonCell = (op) => `.a-cell[data-act="rulerole"][data-role="anon"][data-op="${op}"]`;

test.describe('publish one endpoint', () => {
  test('works: anon on read, warned, previewed, applied, and taken back', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/regions');
    await reset(page);
    await go(page, '#/rules/regions');
    const m = moves(page);

    // Before: regions is readable by anyone signed in, and by nobody else.
    await expect(page.locator('#rule-list .a-perm__cel')).toHaveText("'authenticated' in @user.roles");
    await expect(page.locator('.a-public-warn')).toHaveCount(0);

    await m.click(anonCell('list'));
    await page.waitForTimeout(60);

    // The cell is marked as public, the group carries the warning, and the sentence says "anyone".
    await expect(page.locator(`${anonCell('list')}`)).toHaveClass(/a-cell--public/);
    await expect(page.locator('.a-public-warn')).toBeVisible();
    await expect(page.locator('.a-public-warn')).toContainText('signed in or not');
    await expect(page.locator('#rule-list .a-perm__who')).toContainText('Anyone');
    await shot(page, '04-anon-warned');

    // It is a rule change, in the one queue, and the CEL is what the descriptor will carry.
    expect(await unapplied(page)).toBe(1);
    expect((await changeList(page))[0]).toMatchObject({ kind: 'rules', pointer: '/entities/regions/rules/list' });
    const doc = await working(page);
    expect(doc.entities.regions.rules.list).toContain("'anon' in @user.roles");

    await go(page, '#/schema/preview');

    // Grouped as a Rules change, and the plan says plainly that this is policy, not storage.
    await expect(page.locator('[data-group="rules"]')).toBeVisible();
    await expect(page.locator('[data-group="schema"]')).toHaveCount(0);
    await expect(page.locator('[data-plan="empty"]')).toBeVisible();
    await expect(page.locator('[data-plan="empty"]')).toContainText('changes policy, not storage');
    await expect(page.locator('[data-plan="empty"]')).toContainText('not "no changes"');
    await shot(page, '04-rules-only-preview');

    await page.fill('#apply-reason', 'Publish the region list');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(140);
    expect(await revision(page)).toBe(8);
    expect(await unapplied(page)).toBe(0);

    // Take it back: the same tick, the opposite way, and it is a change again.
    await go(page, '#/rules/regions');
    await page.click(anonCell('list'));
    await page.waitForTimeout(60);
    await expect(page.locator('.a-public-warn')).toHaveCount(0);
    expect(await unapplied(page)).toBe(1);

    await go(page, '#/schema/preview');
    await page.fill('#apply-reason', 'Unpublish it again');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(140);
    expect(await revision(page)).toBe(9);

    // Revision 9's descriptor equals revision 7's for that rule — a round trip with no residue.
    const back = await working(page);
    expect(back.entities.regions.rules.list).toBe("'authenticated' in @user.roles");

    expect(m.count, 'one tick is one move').toBe(1);
    console_.assertClean();
  });

  test('works: the simulator tells anon apart from a signed-in caller', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/regions');
    await reset(page);
    await go(page, '#/rules/regions');

    // anon, before: no rule admits them, and the engine refuses outright.
    await page.click('[data-act="sim"][data-k="user"][data-v=""]');
    await page.waitForTimeout(60);
    await expect(page.locator('[data-verdict]')).toHaveAttribute('data-verdict', 'resolved');
    // regions' list rule IS configured, so the engine resolves a policy — and hands back a
    // predicate the anonymous caller does not satisfy. That is the distinction the screen exists
    // to teach, so it must be stated rather than collapsed into "allowed".
    await expect(page.locator('[data-verdict]')).toContainText('it has not said this caller will see rows');
    await expect(page.locator('#app')).toContainText('a shorter list, not an error');

    // After ticking anon, the predicate itself changes and says so.
    await page.click(anonCell('list'));
    await page.waitForTimeout(60);
    await expect(page.locator('#app')).toContainText("'anon' in @user.roles");

    console_.assertClean();
  });

  test('works: a scoped entity refuses the anonymous caller before any rule is consulted', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/customers');
    await reset(page);
    await go(page, '#/rules/customers');
    await page.click('[data-act="sim"][data-k="user"][data-v=""]');
    await page.waitForTimeout(60);

    await expect(page.locator('[data-verdict]')).toHaveAttribute('data-verdict', 'tenant-guard');
    await expect(page.locator('[data-verdict]')).toContainText('The caller has no tenant, and this entity is tenant-scoped');
    await expect(page.locator('[data-verdict]')).toContainText('before any rule is consulted');
    await shot(page, '04-tenant-guard-verdict');

    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/rules/regions', cell);
      await reset(page);
      await go(page, '#/rules/regions');
      await page.click(anonCell('list'));
      await page.waitForTimeout(60);
      await expect(page.locator('.a-public-warn')).toBeVisible();
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      console_.assertClean();
    });
  }
});
