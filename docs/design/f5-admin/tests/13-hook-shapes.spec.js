/* The two hook shapes, and the one the schema actually declares.

   `$defs/beforeHookList` admits `{ action: { reject } }` and `{ action: { mutate } }` and says why:
   *"Before-actions run in-transaction: reject or mutate only. No network, no external calls."*
   `$defs/afterHookList` admits `{ action: $defs/action }` — five discriminated types.

   Two things can go wrong and both did, in the first version of this editor: offering a network
   action on a before-point, and emitting a shape that is not either of these. Both produce a
   descriptor the apply refuses, which is the defect class the whole design exists to prevent —
   so both are measured rather than reviewed. */

import { test, expect } from '@playwright/test';
import { open, go, guardConsole, reset, working, generated } from './helpers.js';

const FACETS = generated('schema-facets');

async function openHookEditor(page, point) {
  await go(page, '#/schema/work_orders');
  await page.click('[data-act="tab"][data-tab="hooks"]');
  await page.click('[data-kind="new-hook"]');
  await page.waitForTimeout(60);
  await page.click(`[data-act="hookpoint"][data-value="${point}"]`);
  await page.waitForTimeout(60);
}

test('a before-hook emits the schema’s reject shape, not a type discriminator', async ({ page }) => {
  const console_ = guardConsole(page);
  await open(page, '#/schema/work_orders');
  await reset(page);
  await openHookEditor(page, 'beforeCreate');

  await page.click('[data-act="hookkind"][data-value="reject"]');
  await page.fill('#hook-arg', 'An emergency call-out must be priority 1 or 2.');
  await page.fill('#hook-when', 'new.is_emergency && new.priority > 2');
  await page.click('[data-act="addhook"]');
  await page.waitForTimeout(80);

  const doc = await working(page);
  expect(doc.entities.work_orders.hooks.beforeCreate).toEqual([{
    condition: 'new.is_emergency && new.priority > 2',
    action: { reject: 'An emergency call-out must be priority 1 or 2.' },
  }]);
  // Not the shape the first version emitted.
  expect(JSON.stringify(doc.entities.work_orders.hooks)).not.toContain('"type"');
  expect(JSON.stringify(doc.entities.work_orders.hooks)).not.toContain('"message"');

  console_.assertClean();
});

test('a before-hook’s mutate is a payload patch, field to value', async ({ page }) => {
  const console_ = guardConsole(page);
  await open(page, '#/schema/work_orders');
  await reset(page);
  await openHookEditor(page, 'beforeUpdate');

  await page.click('[data-act="hookkind"][data-value="mutate"]');
  await page.selectOption('#hook-arg', 'completed_on');
  // A tagged CEL expression is the schema's `valueOrExpr`; `now()` is in the Mutate profile's
  // allow-listed calls, and `== null` would be refused, so the condition uses has().
  await page.fill('#hook-arg2', '{"$cel": "now()"}');
  await page.fill('#hook-when', "new.status == 'completed' && !has(old.completed_on)");
  await page.click('[data-act="addhook"]');
  await page.waitForTimeout(80);

  const doc = await working(page);
  expect(doc.entities.work_orders.hooks.beforeUpdate).toEqual([{
    condition: "new.status == 'completed' && !has(old.completed_on)",
    action: { mutate: { completed_on: { $cel: 'now()' } } },
  }]);

  console_.assertClean();
});

test('an after-hook emits a discriminated action, and only from a declared endpoint', async ({ page }) => {
  const console_ = guardConsole(page);
  await open(page, '#/schema/work_orders');
  await reset(page);

  // With no endpoint declared, the control says so rather than accepting a name nothing declares.
  await openHookEditor(page, 'afterUpdate');
  await page.click('[data-act="hookkind"][data-value="webhook"]');
  await page.waitForTimeout(60);
  await expect(page.locator('.a-modal')).toContainText('This descriptor declares none');
  await expect(page.locator('#hook-arg')).toBeDisabled();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(60);

  // Declare one, and the same control offers it.
  await go(page, '#/integrations');
  await page.click('[data-kind="new-endpoint"]');
  await page.fill('#ep-name', 'billing-system');
  await page.fill('#ep-url', 'https://billing.internal/hooks/alvo');
  await page.click('[data-act="addendpoint"]');
  await page.waitForTimeout(80);

  await openHookEditor(page, 'afterUpdate');
  await page.click('[data-act="hookkind"][data-value="webhook"]');
  await page.waitForTimeout(60);
  await expect(page.locator('#hook-arg')).toBeEnabled();
  await page.fill('#hook-when', "new.status == 'completed'");
  await page.click('[data-act="addhook"]');
  await page.waitForTimeout(80);

  const doc = await working(page);
  expect(doc.entities.work_orders.hooks.afterUpdate).toEqual([{
    condition: "new.status == 'completed'",
    action: { type: 'webhook', endpoint: 'billing-system' },
  }]);

  console_.assertClean();
});

test('switching a before-point to an after-point replaces an action that cannot survive it', async ({ page }) => {
  const console_ = guardConsole(page);
  await open(page, '#/schema/work_orders');
  await reset(page);
  await openHookEditor(page, 'afterCreate');
  await page.click('[data-act="hookkind"][data-value="webhook"]');
  await page.waitForTimeout(60);

  // Move to a before-point: `webhook` does not exist there, so the chosen kind changes rather than
  // silently producing `{ type: "webhook" }` in a slot the schema refuses it in.
  await page.click('[data-act="hookpoint"][data-value="beforeCreate"]');
  await page.waitForTimeout(60);
  await expect(page.locator('[data-act="hookkind"][data-value="webhook"]')).toHaveCount(0);
  await expect(page.locator('[data-act="hookkind"][data-value="reject"]')).toHaveClass(/a-preset--on/);

  console_.assertClean();
});

test('the declared hooks render from the schema shape, in both directions', async ({ page }) => {
  const console_ = guardConsole(page);
  await open(page, '#/schema/work_orders');
  await reset(page);
  await page.evaluate(() => {
    const doc = window.__alvoPrototype.wc.working.entities.work_orders;
    doc.hooks = {
      beforeCreate: [{ action: { reject: 'No.' } }],
      afterUpdate: [{ condition: "new.status == 'completed'", action: { type: 'webhook', endpoint: 'billing-system' } }],
    };
  });
  await go(page, '#/schema/work_orders');
  await page.click('[data-act="tab"][data-tab="hooks"]');
  await page.waitForTimeout(60);

  await expect(page.locator('#app')).toContainText('Before the write commits');
  await expect(page.locator('#app')).toContainText('reject');
  await expect(page.locator('#app')).toContainText('No.');
  await expect(page.locator('#app')).toContainText('After the write commits');
  await expect(page.locator('#app')).toContainText('billing-system');
  await expect(page.locator('#app')).toContainText("only when");
  // Nothing renders as `unknown`, which is what an unrecognised shape would produce.
  await expect(page.locator('#app')).not.toContainText('unknown');

  console_.assertClean();
});
