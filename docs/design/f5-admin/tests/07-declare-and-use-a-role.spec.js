/* Scenario 7 — Declare a role and use it.

   A new role in Access. Watch it sit unapplied. Apply it. Assign it. Confirm the person's level
   changes.

   What it is really checking: the two speeds. Declaring a role changes the descriptor and waits
   for an apply; assigning one is the identity store and counts on the next request. Between the
   two, an assigned-but-undeclared role is minted by nothing and matches nothing — silently — which
   is the quiet failure the screen exists to make loud. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, unapplied, changeList, revision, working, shot, moves, openPerson, clickVisible, expectNoHorizontalScroll, expectNothingClipped } from './helpers.js';

const MARTIN = 'b2e5d88f-62a3-4c0b-9f41-7d30ac2e5b16';

test.describe('declare a role and use it', () => {
  test('works: declared, unapplied, applied, assigned, and the level moves', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');
    const m = moves(page);

    // Martin reaches nothing today: the descriptor declares no `access` block at all.
    await expect(page.locator(`[data-person="martin@field-service.sk"]`)).toContainText('cannot open the dashboard');

    // Declare the role.
    await m.click('[data-kind="new-role"]');
    await m.fill('#nr-name', 'ops-lead');
    await m.click('[data-act="addrole"]');
    await page.waitForTimeout(80);

    // It is in the ONE queue, as a role-catalogue change, and it is not in effect yet.
    expect(await unapplied(page)).toBe(1);
    expect((await changeList(page))[0]).toMatchObject({ kind: 'roles', pointer: '/auth/roles' });
    await expect(page.locator('[data-pending]')).toBeVisible();
    await expect(page.locator('[data-pending]')).toContainText('role catalogue');
    await shot(page, '07-role-unapplied');

    // Assigning it now is allowed, and it matches nothing — the screen says so rather than
    // pretending the grant works.
    await m.click(`[data-kind="assign"][data-id="${MARTIN}"]`);
    await m.click(`[data-act="assign"][data-id="${MARTIN}"][data-role="ops-lead"]`);
    await page.waitForTimeout(80);
    const martinRow = page.locator('[data-person="martin@field-service.sk"]');
    await expect(martinRow).toContainText('cannot open the dashboard');
    // The role is assigned and not declared, so it is minted by nothing: the row marks it inert
    // and says what that means, rather than showing a grant that quietly matches nothing.
    await expect(martinRow.locator('.a-role--inert')).toHaveCount(1);
    await expect(martinRow).toContainText('is assigned and not declared, so it matches nothing');

    // Apply the catalogue.
    await go(page, '#/schema/preview');
    await expect(page.locator('[data-group="roles"]')).toBeVisible();
    await expect(page.locator('[data-plan="empty"]')).toBeVisible();
    await page.fill('#apply-reason', 'Declare ops-lead');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(140);
    expect(await revision(page)).toBe(8);

    // Now give the level a predicate that names it. This is an `access` change, and the preview
    // says what that costs before Apply is reached.
    await go(page, '#/access');
    await page.fill('[data-act="setaccess"][data-level="viewer"]', "'ops-lead' in @user.roles");
    await page.waitForTimeout(80);
    expect((await changeList(page))[0]).toMatchObject({ kind: 'access', pointer: '/access' });
    await expect(page.locator('[data-pending]')).toContainText('needs <strong>admin</strong>'.replace(/<[^>]+>/g, ''));

    await go(page, '#/schema/preview');
    await expect(page.locator('[data-group="access"]')).toBeVisible();
    await shot(page, '07-access-change');
    await page.fill('#apply-reason', 'ops-lead may read the dashboard');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(140);
    expect(await revision(page)).toBe(9);

    // Martin's level really changed, in the table and in the ladder.
    await go(page, '#/access');
    await expect(page.locator(`[data-person="martin@field-service.sk"]`)).toContainText('viewer');
    await openPerson(page, MARTIN);
    await expect(page.locator('.a-ladder')).toContainText('viewer');
    await expect(page.locator('.a-ladder')).toContainText("'ops-lead' in @user.roles");
    await shot(page, '07-level-changed');

    console_.assertClean();
  });

  test('works: an access predicate the profile forbids is diagnosed at the control', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');

    // A level sees no row, so a field reference compiles in NO profile rather than every profile.
    await page.fill('[data-act="setaccess"][data-level="admin"]', 'owner_id == @user.id');
    await page.waitForTimeout(80);
    await expect(page.locator('#app')).toContainText('a level sees no row');

    // @tenant is excluded deliberately: a level is project-scoped.
    await page.fill('[data-act="setaccess"][data-level="admin"]', "'dispatcher' in @user.roles && @tenant.id == 'x'");
    await page.waitForTimeout(80);
    await expect(page.locator('#app')).toContainText('a level is project-scoped');

    // A role literal the descriptor does not declare is refused at apply, exactly as a rule's is.
    await page.fill('[data-act="setaccess"][data-level="admin"]', "'nope' in @user.roles");
    await page.waitForTimeout(80);
    await expect(page.locator('#app')).toContainText('is not declared in auth.roles');

    console_.assertClean();
  });

  test('works: a role named in a rule cannot be removed, and the button says why', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');

    // `dispatcher` is named in every rule on customers and work_orders, and rules are compiled at
    // apply — so removing it here would make the apply refuse the whole descriptor.
    await expect(page.locator('[data-act="delrole"][data-role="dispatcher"]')).toBeDisabled();
    await expect(page.locator('[data-act="delrole"][data-role="dispatcher"]'))
      .toHaveAttribute('title', /Named in a rule.*refuse the whole descriptor/);

    // `technician` is the interesting case and the example is built on it: technicians reach
    // work orders through `assigned_to == @user.id`, so the role is named in NO rule. Removing it
    // is refused by nothing — it simply stops being minted for the person who holds it, silently.
    // That is not a block; it is a sentence the row has to carry.
    const technician = page.locator('[data-act="delrole"][data-role="technician"]');
    await expect(technician).toBeEnabled();
    await expect(technician).toHaveAttribute('title', /quietly|stops being minted/);
    await expect(page.locator('#app')).toContainText('named in no rule and no level');

    // Removing it is a descriptor change, so Preview and Discard are the way back.
    await technician.click();
    await page.waitForTimeout(80);
    await expect(page.locator('[data-pending]')).toBeVisible();
    await page.click('[data-act="discard"]');
    await page.waitForTimeout(60);
    await expect(page.locator('[data-pending]')).toHaveCount(0);

    console_.assertClean();
  });

  test('works: nobody raises their own level', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');

    // Jana is the signed-in caller; her own row offers no add and no remove.
    const own = page.locator('[data-person="jana@field-service.sk"]');
    await expect(own).toContainText('you');
    await expect(own.locator('[data-act="unassign"]')).toHaveCount(0);
    await expect(own.locator('[data-kind="assign"]')).toHaveCount(0);
    await expect(page.locator('#app')).toContainText('Changing your own roles is refused server-side, not only here');

    console_.assertClean();
  });

  test('works: granting admin asks first, and authenticated is not on the list', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/access');
    await reset(page);
    await go(page, '#/access');

    await clickVisible(page, `[data-kind="assign"][data-id="${MARTIN}"]`);
    await page.waitForTimeout(60);

    // `authenticated` is appended to every signed-in caller and `anon` is the absence of one:
    // offering either would be calling a no-op a grant.
    const modal = page.locator('.a-modal');
    await expect(modal.locator('[data-role="authenticated"]')).toHaveCount(0);
    await expect(modal.locator('[data-role="anon"]')).toHaveCount(0);
    await expect(modal).toContainText('Assigning either is a no-op');

    // admin is offered, and it confirms.
    await page.click(`[data-act="assign"][data-id="${MARTIN}"][data-role="admin"]`);
    await page.waitForTimeout(60);
    await expect(modal).toContainText('Granting');
    const before = await page.evaluate((id) => window.__alvoPrototype.state.membership[id].roleNames.includes('admin'), MARTIN);
    expect(before).toBe(false);

    await page.click(`[data-act="assign"][data-id="${MARTIN}"][data-role="admin"][data-force="1"]`);
    await page.waitForTimeout(80);
    const after = await page.evaluate((id) => window.__alvoPrototype.state.membership[id].roleNames.includes('admin'), MARTIN);
    expect(after).toBe(true);

    // And it says plainly that nothing recorded it.
    await expect(page.locator('[data-membership-log]')).toContainText('nothing recorded it');

    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/access', cell);
      await reset(page);
      await go(page, '#/access');
      await clickVisible(page, '[data-kind="new-role"]');
      await page.waitForTimeout(80);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      console_.assertClean();
    });
  }
});
