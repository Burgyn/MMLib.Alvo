/* Scenario 5 — The layered permission task.

   Dispatchers may change any work order. The assigned technician may change theirs, but only
   while it is not completed. Build it, read it in the simulator, then open the technician in
   Access and confirm the ladder says the same thing.

   What it is really checking: the shape `(who || who) && when` cannot express this, and the
   drawing's first version used exactly that shape — so a condition meant for technicians silently
   locked dispatchers out of every non-scheduled record. Conditions belong to the branch. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, working, revision, shot, moves, expectNoHorizontalScroll, expectNothingClipped , openPerson } from './helpers.js';

const cell = (role, op) => `.a-cell[data-act="rulerole"][data-role="${role}"][data-op="${op}"]`;
const ownerCell = (field, op) => `.a-cell[data-act="ruleowner"][data-field="${field}"][data-op="${op}"]`;

test.describe('the layered permission', () => {
  test('works: one branch carries the condition, and the other does not', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/work_orders');
    await reset(page);
    await go(page, '#/rules/work_orders');
    const m = moves(page);

    // The example already admits dispatchers, admins and the assignee on `update`. The task is
    // to narrow ONLY the assignee's branch.
    const start = await working(page);
    expect(start.entities.work_orders.rules.update)
      .toBe("'dispatcher' in @user.roles || 'admin' in @user.roles || assigned_to == @user.id");

    // Open the change editor for `update` and narrow the owner branch, not the column.
    await m.click(ownerCell('assigned_to', 'update'));   // toggles it off
    await m.click(ownerCell('assigned_to', 'update'));   // and back on — this also opens the editor
    await page.waitForTimeout(80);
    await expect(page.locator('#rule-update')).toHaveClass(/a-perm--open/);

    // The owner branch is the third one. Narrow it.
    const ownerBranchIndex = await page.evaluate(() => {
      const wc = window.__alvoPrototype.wc;
      const cel = wc.working.entities.work_orders.rules.update;
      return cel.split('||').findIndex((part) => part.includes('assigned_to'));
    });
    await m.click(`[data-act="condadd"][data-op="update"][data-b="${ownerBranchIndex}"]`);
    await page.waitForTimeout(60);

    // Set it to: status is not completed.
    await page.selectOption(`[data-act="condfield"][data-op="update"][data-b="${ownerBranchIndex}"][data-i="0"]`, 'status');
    await page.waitForTimeout(60);
    await page.selectOption(`[data-act="condop"][data-op="update"][data-b="${ownerBranchIndex}"][data-i="0"]`, '!=');
    await page.waitForTimeout(60);
    await page.selectOption(`[data-act="condvalue"][data-op="update"][data-b="${ownerBranchIndex}"][data-i="0"]`, 'completed');
    await page.waitForTimeout(80);

    const doc = await working(page);
    const cel = doc.entities.work_orders.rules.update;

    // The dispatcher branch is untouched; only the owner branch carries the test.
    expect(cel).toContain("'dispatcher' in @user.roles");
    expect(cel).toContain("(assigned_to == @user.id && status != 'completed')");
    expect(cel.split('||')[0].trim()).toBe("'dispatcher' in @user.roles");
    // The shape `(a || b) && c` — which would lock dispatchers out too — is NOT what was produced.
    expect(cel).not.toMatch(/^\(.*\|\|.*\)\s*&&/);

    // The sentence says it in words, and distinguishes the two branches with a semicolon.
    await expect(page.locator('#rule-update .a-perm__who')).toContainText('Dispatchers');
    await expect(page.locator('#rule-update .a-perm__who')).toContainText('while');
    await expect(page.locator('#rule-update .a-perm__who')).toContainText('is not');

    // The null trap is called out, because `status` is required here but the warning must appear
    // wherever an "is not" test runs over a field that may be empty.
    await shot(page, '05-branch-condition');

    // The matrix marks the condition on the owner row, and leaves the dispatcher row plain.
    await expect(page.locator(ownerCell('assigned_to', 'update'))).toContainText('1 condition');
    await expect(page.locator(cell('dispatcher', 'update'))).not.toContainText('condition');

    console_.assertClean();
  });

  test('works: the simulator reads the predicate, and the Access ladder says the same thing', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/work_orders');
    await reset(page);

    // Put the rule in place directly — this test is about what the two screens AGREE on.
    await page.evaluate(() => {
      window.__alvoPrototype.wc.working.entities.work_orders.rules.update =
        "'dispatcher' in @user.roles || 'admin' in @user.roles || (assigned_to == @user.id && status != 'completed')";
      window.__alvoPrototype.wc.applied.entities.work_orders.rules.update =
        "'dispatcher' in @user.roles || 'admin' in @user.roles || (assigned_to == @user.id && status != 'completed')";
    });
    await go(page, '#/rules/work_orders');

    // The simulator, for the technician, on update.
    await page.click('[data-act="sim"][data-k="user"][data-v="c3f6e990-73b4-4d1c-a052-8e41bd3f6c27"]');
    await page.click('[data-act="sim"][data-k="operation"][data-v="update"]');
    await page.waitForTimeout(80);

    await expect(page.locator('[data-verdict]')).toHaveAttribute('data-verdict', 'resolved');
    await expect(page.locator('#app')).toContainText("status != 'completed'");
    // An update is judged by both predicates, and the screen shows both.
    await expect(page.locator('#app')).toContainText('USING — the read predicate');
    await expect(page.locator('#app')).toContainText('WITH CHECK — the write predicate');
    // And it says what failing the read predicate looks like for `update`: a 404, not an error page.
    await expect(page.locator('#app')).toContainText('deliberately indistinguishable from');
    await shot(page, '05-simulator-update');

    // Access says the same thing, from the applied descriptor, in the ladder.
    await go(page, '#/access');
    await openPerson(page, 'c3f6e990-73b4-4d1c-a052-8e41bd3f6c27');

    const ladder = page.locator('.a-ladder');
    await expect(ladder).toContainText('technician');
    await expect(ladder).toContainText('billing-manager');   // assigned, not declared, shown inert
    await expect(ladder).toContainText('work_orders');
    // The "own" answer for update, with the same condition the simulator showed.
    await expect(ladder).toContainText('only the rows assigned_to names them in');
    await expect(ladder).toContainText("status != 'completed'");
    await shot(page, '05-access-ladder');

    console_.assertClean();
  });

  test('works: the ladder reads the applied descriptor, not the draft', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/rules/work_orders');
    await reset(page);
    await go(page, '#/rules/work_orders');

    // Grant technicians `create` in the working copy only.
    await page.click(cell('technician', 'create'));
    await page.waitForTimeout(60);
    const doc = await working(page);
    expect(doc.entities.work_orders.rules.create).toContain("'technician' in @user.roles");

    // The ladder must still say no — callers get the applied revision.
    await go(page, '#/access');
    await openPerson(page, 'c3f6e990-73b4-4d1c-a052-8e41bd3f6c27');
    await expect(page.locator('.a-rung').last()).toContainText('reads the applied descriptor, not your working copy');

    const createVerdicts = await page.evaluate(() => {
      const rows = [...document.querySelectorAll('.a-can')];
      const workOrders = rows.find((r) => r.textContent.includes('24,680'));
      return [...workOrders.querySelectorAll('.a-can__v')].map((v) => v.textContent.trim());
    });
    // list, get, create, update, delete — create is the third, and it is still a dash.
    expect(createVerdicts[2]).toBe('—');

    console_.assertClean();
  });

  for (const cell_ of MATRIX) {
    test(`looks right: ${cell_.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/rules/work_orders', cell_);
      await reset(page);
      await go(page, '#/rules/work_orders');
      await page.click('[data-act="ruleopen"][data-op="update"]');
      await page.waitForTimeout(80);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      console_.assertClean();
    });
  }
});
