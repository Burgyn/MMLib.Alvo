import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, expectNoHorizontalScroll, expectNoVisiblePlaceholder } from './helpers.js';

/* Every route the shell offers, plus the ones only a link reaches. If one of these throws, the
   screen it belongs to is blank and no other spec in this directory means anything. */
const ROUTES = [
  '#/overview',
  '#/schema',
  '#/schema/work_orders',
  '#/schema/customers',
  '#/schema/regions',
  '#/schema/preview',
  '#/schema/transfer',
  '#/data',
  '#/data/work_orders',
  '#/data/customers',
  '#/data/regions',
  '#/rules',
  '#/rules/work_orders',
  '#/access',
  '#/integrations',
  '#/history',
  '#/automations',
  '#/functions',
  '#/settings',
  '#/notes',
  '#/welcome/1',
  '#/welcome/2',
];

for (const cell of MATRIX) {
  test.describe(`smoke — ${cell.name}`, () => {
    test('every route renders, says something, and logs nothing', async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, ROUTES[0], cell);

      for (const route of ROUTES) {
        await go(page, route);
        const text = await page.locator('#app').innerText();
        expect(text.trim().length, `${route} rendered nothing`).toBeGreaterThan(40);
        await expectNoVisiblePlaceholder(page);
        await expectNoHorizontalScroll(page);
      }

      console_.assertClean();
    });
  });
}

test('an unknown route does not blank the shell', async ({ page }) => {
  const console_ = guardConsole(page);
  await open(page, '#/overview');
  await go(page, '#/this-route-does-not-exist');
  await expect(page.locator('#app')).not.toBeEmpty();
  console_.assertClean();
});
