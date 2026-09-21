/* Scenario 1 — First run.

   An empty instance. Sign in as the bootstrap administrator, name the project, land somewhere
   useful with no entities yet.

   What it is really checking: that the wizard does not describe a product that does not exist.
   The bootstrap administrator already exists before this page can be reached, there is no default
   password, and seeding never resets one — so the first step is signing in, not creating an
   account. And with multi-tenancy on, an operator with no tenant grant lands on a Data screen
   that refuses two entities out of three, which is why there is a third step. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, expectNoHorizontalScroll, expectNothingClipped, expectNoVisiblePlaceholder, shot, moves } from './helpers.js';

test.describe('first run', () => {
  test('works: three steps, and the instance ends up modelled', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/welcome/1');
    const m = moves(page);

    // Step 1 is a sign-in, not an account creation.
    await expect(page.locator('#app')).toContainText('Sign in as the bootstrap administrator');
    await expect(page.locator('#app')).toContainText('The image ships no credential and there is no default password');
    await expect(page.locator('#app')).not.toContainText('Create the first account');
    await expect(page.locator('#w-email')).toHaveValue('jana@field-service.sk');

    await m.click('[data-route="#/welcome/2"]');

    // Step 2 names the project. Continue stays disabled until it has a name.
    await expect(page.locator('[data-act="go"][data-route="#/welcome/3"]')).toBeDisabled();
    await m.fill('#w-name', 'acme-billing');
    await m.fill('#w-desc', 'Invoices and the customers they belong to.');
    await m.click('[data-act="wizardtenancy"]');
    await expect(page.locator('[data-act="go"][data-route="#/welcome/3"]')).toBeEnabled();
    await m.click('[data-route="#/welcome/3"]');

    // Step 3 is the tenant grant §2.7 needs, and it exists because the drawing found the gap.
    await expect(page.locator('#app')).toContainText('Which tenant do you act in?');
    await expect(page.locator('#app')).toContainText('a caller with none is refused them');
    await expect(page.locator('#app')).toContainText('Alvo stores no name for a tenant');
    await m.click('[data-act="finishwizard"]');

    // It lands on Schema's empty state, which says what to do next rather than "No data".
    await page.waitForTimeout(80);
    expect(page.url()).toContain('#/schema');
    await expect(page.locator('#app')).toContainText('No entities yet');
    await expect(page.locator('#app')).toContainText('Name it in the plural');
    await expect(page.locator('[data-kind="new-entity"]').first()).toBeEnabled();

    // The project really is what the wizard said, at revision 1, with the operator's tenant set.
    const applied = await page.evaluate(() => ({
      name: window.__alvoPrototype.wc.working.name,
      revision: window.__alvoPrototype.wc.revision,
      entities: Object.keys(window.__alvoPrototype.wc.working.entities ?? {}),
      unapplied: window.__alvoPrototype.count(),
      tenant: window.__alvoPrototype.state.membership[window.__alvoPrototype.state.signedIn].tenant,
    }));
    expect(applied).toMatchObject({ name: 'acme-billing', revision: 1, entities: [], unapplied: 0 });
    expect(applied.tenant).toBeTruthy();

    // usable: six moves from an empty instance to a modelling screen.
    expect(m.count, 'first run should stay under eight moves').toBeLessThanOrEqual(8);

    console_.assertClean();
    await shot(page, '01-first-run-empty-schema');
  });

  test('works: a signed-in operator with no tenant is told so rather than shown an empty page', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    // Take the tenant away — the state a first run reaches if step 3 is skipped.
    await page.evaluate(() => {
      const s = window.__alvoPrototype.state;
      s.membership[s.signedIn].tenant = null;
    });
    await go(page, '#/data/customers');

    await expect(page.locator('[data-tenantless]')).toBeVisible();
    await expect(page.locator('#app')).toContainText('before any rule is consulted');
    await expect(page.locator('#app')).toContainText('that would be 200 with an empty page');
    await expect(page.locator('#app')).not.toContainText('No records yet');

    // The global entity is unaffected, which is the whole point of the distinction.
    await go(page, '#/data/regions');
    await expect(page.locator('table.a-grid')).toBeVisible();
    await expect(page.locator('#app')).toContainText('global — every tenant reads these rows');

    console_.assertClean();
    await shot(page, '01-first-run-tenantless');
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      for (const step of ['#/welcome/1', '#/welcome/2', '#/welcome/3']) {
        await open(page, step, cell);
        await expectNoHorizontalScroll(page);
        await expectNothingClipped(page);
        await expectNoVisiblePlaceholder(page);
      }
      console_.assertClean();
    });
  }
});
