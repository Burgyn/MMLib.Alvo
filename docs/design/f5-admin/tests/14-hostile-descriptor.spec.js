/* A descriptor somebody else wrote.

   The Import box is the one place this drawing takes input it did not produce, and the threat
   model is ordinary: *somebody sends you a descriptor to look at*. Two things make that sharper
   than an `alert(1)` here — every screen builds HTML with template strings into `innerHTML`, and
   the test server is rooted at the REPOSITORY, so the page's origin covers the whole checkout.

   Two layers are measured, because either alone is one mistake from failing:

   1. **The boundary.** A name the frozen schema's `propertyNames` pattern refuses is refused at
      Import — which is the product behaviour anyway, since the apply refuses it, and the design's
      own rule is that a control must never produce a descriptor the apply would reject.
   2. **The sinks.** Nothing descriptor-derived reaches `innerHTML` unescaped, so a name that got
      past layer one still renders as text.

   This spec is the second half of the `alvo-security-core-review` pass; the checklist half is in
   SESSION.md. */

import { test, expect } from '@playwright/test';
import { open, go, guardConsole, reset, working, generated } from './helpers.js';

const FACETS = generated('schema-facets');

/** A descriptor that is valid apart from one hostile name. */
const payload = '"><img src=x onerror=window.__pwned=1>';

function hostile(overrides) {
  return JSON.stringify({
    $schema: 'https://alvo.dev/schema/v1/project.json',
    apiVersion: 'alvo.dev/v1',
    name: 'borrowed',
    entities: { things: { fields: { title: { type: 'string', maxLength: 40 } } } },
    ...overrides,
  });
}

async function importDescriptor(page, json) {
  await go(page, '#/schema/transfer');
  await page.fill('#import-json', json);
  await page.click('[data-act="import"]');
  await page.waitForTimeout(120);
}

test.describe('an imported descriptor', () => {
  test('a valid one is accepted and previewed', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/transfer');
    await reset(page);
    await importDescriptor(page, hostile({}));

    expect(page.url()).toContain('#/schema/preview');
    const doc = await working(page);
    expect(Object.keys(doc.entities)).toEqual(['things']);
    console_.assertClean();
  });

  /* One case per name the schema constrains. Each is a descriptor the apply would refuse, so
     refusing it here is the product behaviour — and each carries a payload, so a pass is also the
     XSS being closed at the boundary. */
  const REFUSED = [
    ['an entity name', { entities: { [`things${payload}`]: { fields: { a: { type: 'string' } } } } }],
    ['a field name', { entities: { things: { fields: { [`title${payload}`]: { type: 'string' } } } } }],
    ['a role name', { auth: { roles: [`dispatcher${payload}`] } }],
    ['a format name', { formats: { [`fmt${payload}`]: { pattern: 'x' } } }],
    ['a ref target', { entities: { things: { fields: { a: { type: 'ref', entity: `other${payload}` } } } } }],
    ['an index column', { entities: { things: { fields: { title: { type: 'string' } }, indexes: [{ fields: [`title${payload}`] }] } } }],
    ['the project name', { name: `borrowed${payload}` }],
    ['an access level nobody declares', { access: { superuser: "'admin' in @user.roles" } }],
    ['a field type', { entities: { things: { fields: { a: { type: 'script' } } } } }],
    ['a decimal with no precision', { entities: { things: { fields: { a: { type: 'decimal' } } } } }],
    ['an enum with no values', { entities: { things: { fields: { a: { type: 'enum' } } } } }],
    ['a ref with no entity', { entities: { things: { fields: { a: { type: 'ref' } } } } }],
  ];

  for (const [what, overrides] of REFUSED) {
    test(`is refused when ${what} is one the schema cannot carry`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/schema/transfer');
      await reset(page);
      await importDescriptor(page, hostile(overrides));

      // Refused, with the reason, and the working copy is untouched.
      expect(page.url()).toContain('#/schema/transfer');
      await expect(page.locator('.a-error')).toBeVisible();
      await expect(page.locator('.a-error')).toContainText('The apply would refuse this descriptor');
      const doc = await working(page);
      expect(Object.keys(doc.entities)).toEqual(['regions', 'customers', 'work_orders']);

      // And nothing executed.
      expect(await page.evaluate(() => window.__pwned)).toBeUndefined();
      console_.assertClean();
    });
  }

  test('a name that got past the boundary still renders as text, not as markup', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);

    /* The second layer, measured on its own: this bypasses the Import check entirely and writes
       the hostile document straight into the working copy, which is what a missed validation
       would do. Every screen must still render it inert. */
    await page.evaluate((mark) => {
      const doc = window.__alvoPrototype.wc.working;
      doc.entities[`evil${mark}`] = {
        description: `desc${mark}`,
        fields: {
          [`col${mark}`]: { type: 'string', description: `field${mark}` },
          link: { type: 'ref', entity: `target${mark}`, onDelete: 'restrict' },
        },
        indexes: [{ fields: [`col${mark}`] }],
        rules: { list: `'role${mark}' in @user.roles` },
      };
      doc.auth = { roles: [`role${mark}`] };
      doc.formats = { [`fmt${mark}`]: { pattern: 'x' } };
      doc.templates = { [`tpl${mark}`]: { subject: `subj${mark}` } };
      doc.webhooks = { endpoints: [{ name: `ep${mark}`, url: `https://x/${mark}` }] };
    }, payload);

    const routes = ['#/overview', '#/schema', `#/schema/evil${payload}`, '#/data', '#/rules',
      '#/access', '#/integrations', '#/schema/preview', '#/schema/transfer', '#/settings', '#/notes'];
    for (const route of routes) {
      await go(page, route);
      expect(await page.evaluate(() => window.__pwned), `${route} executed the payload`).toBeUndefined();
      // No element was created from the payload anywhere in the shell.
      const injected = await page.evaluate(() => document.querySelectorAll('#app img[src="x"]').length);
      expect(injected, `${route} turned a name into an element`).toBe(0);
    }

    // The entity editor and every tab of it, which is where the most names are rendered.
    await go(page, '#/schema/work_orders');
    for (const tab of ['fields', 'relationships', 'rules', 'hooks', 'indexes', 'api']) {
      await page.click(`[data-act="tab"][data-tab="${tab}"]`);
      await page.waitForTimeout(40);
      expect(await page.evaluate(() => window.__pwned), `the ${tab} tab executed the payload`).toBeUndefined();
      expect(await page.evaluate(() => document.querySelectorAll('#app img[src="x"]').length)).toBe(0);
    }

    // And the hook editor, whose selects are built from template and endpoint names.
    await page.click('[data-act="tab"][data-tab="hooks"]');
    await page.waitForTimeout(40);
    await page.click('[data-kind="new-hook"]');
    await page.click('[data-act="hookpoint"][data-value="afterUpdate"]');
    await page.click('[data-act="hookkind"][data-value="webhook"]');
    await page.waitForTimeout(60);
    expect(await page.evaluate(() => window.__pwned)).toBeUndefined();
    expect(await page.evaluate(() => document.querySelectorAll('img[src="x"]').length)).toBe(0);

    console_.assertClean();
  });

  test('a catastrophically backtracking format pattern does not hang the tab', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/transfer');
    await reset(page);

    // Straight into the working copy, again bypassing the boundary check that refuses it.
    await page.evaluate(() => {
      const doc = window.__alvoPrototype.wc.working;
      doc.formats = { ...doc.formats, evil: { pattern: '(a+)+$' } };
      doc.entities.work_orders.fields.reference.format = 'evil';
    });
    await go(page, '#/data/work_orders');
    await page.click('[data-kind="record-new"]:visible');
    await page.waitForTimeout(80);

    const started = Date.now();
    await page.fill('#rf-reference', 'a'.repeat(30) + '!');
    await page.click('[data-act="submitrecord"]');
    await page.waitForTimeout(120);
    expect(Date.now() - started, 'validation hung on a backtracking pattern').toBeLessThan(5000);

    console_.assertClean();
  });

  test('a nested-quantifier pattern is refused at the boundary, with the reason', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/transfer');
    await reset(page);
    await importDescriptor(page, hostile({ formats: { evil: { pattern: '(a+)+$' } } }));
    await expect(page.locator('.a-error')).toContainText('backtracks exponentially');
    await expect(page.locator('.a-error')).toContainText('refuses what it cannot check cheaply');
    console_.assertClean();
  });

  test('a pattern that is not a regular expression is a validation answer, not a throw', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema/transfer');
    await reset(page);
    await importDescriptor(page, hostile({ formats: { broken: { pattern: '([unclosed' } } }));
    await expect(page.locator('.a-error')).toContainText('not a valid regular expression');
    console_.assertClean();
  });

  test('the patterns it validates against are the schema’s own, not a copy', async () => {
    // If the schema's propertyNames pattern changes, the generator picks it up and this spec
    // keeps testing the real rule rather than a snapshot of it.
    expect(FACETS.namePatterns.entity).toBe('^[a-z][a-z0-9_]{0,62}$');
    expect(FACETS.namePatterns.field).toBe('^[a-z][a-z0-9_]{0,62}$');
    expect(FACETS.namePatterns.identifier).toBe('^[a-z][a-z0-9_-]{0,62}$');
    // None of them admits a character that means anything in HTML.
    for (const pattern of Object.values(FACETS.namePatterns)) {
      for (const dangerous of ['<', '>', '"', "'", '&']) {
        expect(new RegExp(pattern).test(`a${dangerous}b`), `${pattern} admits ${dangerous}`).toBe(false);
      }
    }
  });
});
