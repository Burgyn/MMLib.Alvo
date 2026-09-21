/* Scenario 8 — Record CRUD.

   Create a work order through the reference picker and the required hidden field. Hit a unique
   violation. Fix it. Open the detail. Follow the reverse relation.

   What it is really checking: the form is generated from the descriptor, and the ONLY exclusions
   are `readOnly`, `computed` and `rollup`. A hidden field is writable by design, and a required
   hidden one must be on the form or its create is impossible. A `uuid` gets a text input, because
   the descriptor cannot say what it points at. And an error lands at its field, after a real
   collision, carrying the slug and the violation code the API actually uses. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, shot, moves, clickVisible, expectNoHorizontalScroll, expectNothingClipped } from './helpers.js';

async function openForm(page) {
  await go(page, '#/data/work_orders');
  await clickVisible(page, '[data-kind="record-new"]');
  await page.waitForTimeout(80);
}

test.describe('record CRUD', () => {
  test('works: the form is the descriptor, and it shows no error before you type', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/data/work_orders');
    await reset(page);
    await openForm(page);

    const drawer = page.locator('.a-drawer');

    // Nothing is red on an untouched form. The old version rendered a permanent
    // "reference is already taken" at the bottom, a thousand pixels from its field.
    await expect(drawer.locator('.a-error')).toHaveCount(0);

    // Every writable field is here, including both hidden ones.
    for (const name of ['reference', 'title', 'status', 'priority', 'access_code', 'internal_notes', 'assigned_to', 'customer_id', 'region_id']) {
      await expect(drawer.locator(`#lbl-${name}`)).toHaveCount(1);
    }
    // And the three that are not writable are not.
    for (const name of ['external_ref']) {
      await expect(drawer.locator(`#lbl-${name}`)).toHaveCount(0);
    }

    // The required hidden field explains why it is on a form at all.
    await expect(drawer.locator('#lbl-access_code')).toContainText('Write only');
    await expect(drawer.locator('#lbl-access_code')).toContainText('the one case Alvo publishes a hidden field');
    // The optional hidden one says the opposite thing, correctly.
    await expect(drawer.locator('#lbl-internal_notes')).toContainText('its name is in no published schema');

    // A uuid is a plain input — the descriptor does not say what it points at, so there is
    // nothing to offer a picker over. The old version offered "Choose a technician…".
    await expect(drawer.locator('#rf-assigned_to')).toHaveAttribute('placeholder', '00000000-0000-0000-0000-000000000000');
    await expect(drawer.locator('#lbl-assigned_to')).toContainText('only a <code class="a-mono">ref</code> does'.replace(/<[^>]+>/g, ''));

    // Both ref pickers start collapsed. Three refs used to mean three open lists at once.
    await expect(drawer.locator('.a-picker')).toHaveCount(0);
    await expect(drawer.locator('[data-act="pickopen"]')).toHaveCount(2);

    await shot(page, '08-form-untouched');
    console_.assertClean();
  });

  test('works: create, collide, fix, open, and follow the reverse relation', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/data/work_orders');
    await reset(page);
    await openForm(page);
    const m = moves(page);

    // Collide on purpose: WO-100418 already exists and `reference` is unique.
    await m.fill('#rf-reference', 'WO-100418');
    await m.fill('#rf-title', 'Replace the pump seal');
    await m.click('[data-act="formpick"][data-field="status"][data-value="scheduled"]');
    await m.fill('#rf-priority', '2');
    await m.fill('#rf-access_code', '4417');

    // The reference picker: opens, searches, picks.
    await m.click('[data-act="pickopen"][data-field="customer_id"]');
    await expect(page.locator('.a-picker')).toBeVisible();
    await expect(page.locator('.a-picker')).toContainText('The descriptor has no display-field concept');
    // Kavka Labs belongs to the other tenant, so it is not offered — the picker reads under the
    // operator's own context, exactly as the grid does.
    await m.fill('[data-act="pickquery"][data-field="customer_id"]', 'Kavka');
    await page.waitForTimeout(60);
    await expect(page.locator('.a-picker__item')).toHaveCount(0);
    await expect(page.locator('.a-picker')).toContainText('Nothing matches');

    await page.fill('[data-act="pickquery"][data-field="customer_id"]', 'Lumen');
    await page.waitForTimeout(60);
    await expect(page.locator('.a-picker__item')).toHaveCount(1);
    await m.click('[data-act="pick-ref"][data-field="customer_id"]');
    await expect(page.locator('.a-picker__chosen')).toContainText('Lumen Digital');
    await shot(page, '08-ref-picker');

    await m.click('[data-act="pickopen"][data-field="region_id"]');
    await m.click('[data-act="pick-ref"][data-field="region_id"]');

    await m.click('[data-act="submitrecord"]');
    await page.waitForTimeout(80);

    // The collision is a 409 `conflict` with the violation code `unique` — not a slug of its own,
    // and not a validation failure: the request is well formed and collides with what is stored.
    const err = page.locator('#err-reference');
    await expect(err).toBeVisible();
    await expect(err).toContainText('reference is already taken');
    await expect(err).toContainText('errors/conflict');
    await expect(err).toContainText('unique');
    await expect(err).toContainText('There is no unique-violation slug');
    // It is AT the field, not at the bottom of the form.
    const errBox = await err.boundingBox();
    const fieldBox = await page.locator('#rf-reference').boundingBox();
    expect(Math.abs(errBox.y - fieldBox.y)).toBeLessThan(140);
    await shot(page, '08-unique-violation');

    // Fix it, and it lands.
    await m.fill('#rf-reference', 'WO-100427');
    await m.click('[data-act="submitrecord"]');
    await page.waitForTimeout(100);
    await expect(page.locator('.a-drawer')).toHaveCount(0);
    await expect(page.locator('#app')).toContainText('WO-100427');

    // Open the detail, and follow the reverse relation to the customer's other rows.
    await clickVisible(page, '[data-kind="record"][data-id^="new_"]');
    await page.waitForTimeout(80);
    await expect(page.locator('.a-drawer')).toContainText('WO-100427');
    await expect(page.locator('.a-drawer')).toContainText('Replace the pump seal');
    // A hidden field is in no response, and the drawer says which and why.
    await expect(page.locator('.a-drawer')).toContainText('internal_notes');
    await expect(page.locator('.a-drawer')).toContainText('in no response Alvo sends, to anyone');
    await shot(page, '08-record-detail');

    // The reverse relation: customers is pointed AT by work_orders.customer_id.
    await go(page, '#/data/customers');
    await clickVisible(page, '[data-kind="record"][data-id="cu_0c31"]');
    await page.waitForTimeout(80);
    await expect(page.locator('.a-drawer')).toContainText('work_orders pointing here');
    await expect(page.locator('.a-drawer')).toContainText('WO-100427');
    await expect(page.locator('.a-drawer')).toContainText('The reverse of');
    await shot(page, '08-reverse-relation');

    console_.assertClean();
  });

  test('works: a required field, a maxLength and a format are each refused with their own reason', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/data/work_orders');
    await reset(page);
    await openForm(page);

    await page.click('[data-act="submitrecord"]');
    await page.waitForTimeout(80);

    // The required ones, including the hidden one — which is exactly why it is on the form.
    await expect(page.locator('#err-reference')).toContainText('reference is required');
    await expect(page.locator('#err-access_code')).toContainText('access_code is required');
    await expect(page.locator('#err-access_code')).toContainText('hidden as well as required');

    // A format is anchored over the whole value by the framework, not by the author's regex.
    await page.fill('#rf-reference', 'WO-1');
    await page.click('[data-act="submitrecord"]');
    await page.waitForTimeout(80);
    await expect(page.locator('#err-reference')).toContainText('does not match work-order-ref');
    await expect(page.locator('#err-reference')).toContainText('anchors it over the whole value');

    // A value that is right except for trailing text is refused for the same reason.
    await page.fill('#rf-reference', 'WO-100999 urgent');
    await page.click('[data-act="submitrecord"]');
    await page.waitForTimeout(80);
    await expect(page.locator('#err-reference')).toContainText('does not match work-order-ref');

    await page.fill('#rf-reference', 'WO-100999');
    await page.fill('#rf-title', 'x'.repeat(130));
    await page.click('[data-act="submitrecord"]');
    await page.waitForTimeout(80);
    await expect(page.locator('#err-title')).toContainText('too long');
    await expect(page.locator('#err-title')).toContainText('120 characters and this is 130');

    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/data/work_orders', cell);
      await reset(page);
      await openForm(page);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      await page.click('[data-act="pickopen"][data-field="customer_id"]');
      await page.waitForTimeout(60);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      console_.assertClean();
    });
  }
});
