/* Scenario 10 — The honest edges.

   Integrations (unsigned deliveries, an unused template), Automations and Functions as *not yet*,
   and a refused facet in the field editor.

   What it is really checking: the two classes of "not yet" do not look alike, and neither one is
   paraphrased. A WARNED block applies and does nothing — the section exists and quotes the
   framework's own sentence. A REFUSED feature is rejected at apply — the control must not exist,
   or must be inert carrying the refusal verbatim. The prose is served as written: a second
   wording is a third spelling of one truth. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, shot, expectNoHorizontalScroll, expectNothingClipped, deadControls, generated } from './helpers.js';

const CAPABILITIES = generated('capabilities');

test.describe('the honest edges', () => {
  test('works: every refusal the framework publishes is quoted verbatim somewhere', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/integrations');
    await reset(page);

    // Gather the text of every screen a refusal can appear on.
    const seen = new Set();
    const routes = ['#/integrations', '#/automations', '#/functions', '#/schema/work_orders'];
    for (const route of routes) {
      await go(page, route);
      const text = await page.locator('#app').innerText();
      for (const r of CAPABILITIES.refused) if (text.includes(r.consequence)) seen.add(r.slot);
    }

    // The field editor's own refusals sit behind a disclosure — present and inert, so the
    // decision is visibly made rather than forgotten. A person must be able to reach them.
    await go(page, '#/schema/work_orders');
    await page.click('[data-act="field"][data-field="reference"]');
    await page.waitForTimeout(60);
    await page.locator('details.a-disclose summary').first().click();
    await page.waitForTimeout(60);
    let text = await page.locator('#app').innerText();
    for (const r of CAPABILITIES.refused) if (text.includes(r.consequence)) seen.add(r.slot);

    // New entity: softDelete.
    await go(page, '#/schema');
    await page.click('[data-kind="new-entity"]');
    await page.waitForTimeout(60);
    text = await page.locator('#app').innerText();
    for (const r of CAPABILITIES.refused) if (text.includes(r.consequence)) seen.add(r.slot);

    // New template: bodyFile and email.data.
    await go(page, '#/integrations');
    await page.click('[data-kind="new-template"]');
    await page.waitForTimeout(60);
    text = await page.locator('#app').innerText();
    for (const r of CAPABILITIES.refused) if (text.includes(r.consequence)) seen.add(r.slot);

    // A JSONata transform on an after-hook that leaves the process. (An AFTER point: a before-hook
    // has no network action to reshape the payload of.)
    await go(page, '#/schema/work_orders');
    await page.click('[data-act="tab"][data-tab="hooks"]');
    await page.click('[data-kind="new-hook"]');
    await page.click('[data-act="hookpoint"][data-value="afterUpdate"]');
    await page.waitForTimeout(60);
    await page.click('[data-act="hookkind"][data-value="webhook"]');
    await page.waitForTimeout(60);
    text = await page.locator('#app').innerText();
    for (const r of CAPABILITIES.refused) if (text.includes(r.consequence)) seen.add(r.slot);

    // A rollup's `where`. (The tab follows you across entities, which is what the entity bar is
    // for, so the Fields tab has to be chosen again after the hooks detour.)
    await go(page, '#/schema/customers');
    await page.click('[data-act="tab"][data-tab="fields"]');
    await page.click('[data-act="field"][data-field="name"]');
    await page.click('[data-act="derive"][data-kind="rollup"]');
    await page.waitForTimeout(60);
    text = await page.locator('#app').innerText();
    for (const r of CAPABILITIES.refused) if (text.includes(r.consequence)) seen.add(r.slot);

    const missing = CAPABILITIES.refused.map((r) => r.slot).filter((slot) => !seen.has(slot));
    expect(missing, 'every refused slot must be quoted somewhere, verbatim').toEqual([]);

    console_.assertClean();
  });

  test('works: Integrations serves the warning once, and the refused actions are inert', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/integrations');
    await reset(page);
    await go(page, '#/integrations');

    const webhooks = CAPABILITIES.warned.find((w) => w.block === 'webhooks').consequence;
    const templates = CAPABILITIES.warned.find((w) => w.block === 'templates').consequence;
    const text = await page.locator('#app').innerText();

    // Verbatim, and ONCE — the old screen repeated a REWRITTEN version under every endpoint.
    expect(text).toContain(webhooks);
    expect(text.split(webhooks).length - 1, 'said once, not under every row').toBe(1);
    expect(text).toContain(templates);
    expect(text).not.toContain('Treat the endpoint as unauthenticated until signing lands');

    // The three refused action types are shown, disabled, with their own reason.
    for (const type of ['function', 'http.call', 'entity.update']) {
      await expect(page.locator('#app')).toContainText(type);
    }
    await expect(page.locator('#app')).toContainText('refused at apply');
    await shot(page, '10-integrations');

    // And the hook editor offers what the point actually admits, which is not one list.
    await go(page, '#/schema/work_orders');
    await page.click('[data-act="tab"][data-tab="hooks"]');
    await page.click('[data-kind="new-hook"]');
    await page.waitForTimeout(60);

    // A BEFORE hook runs in the transaction, and $defs/beforeHookList gives it no spelling for a
    // network call at all. Offering `webhook` here would be a control whose only possible output
    // is a descriptor the apply rejects.
    await expect(page.locator('[data-act="hookkind"][data-value="reject"]')).toBeEnabled();
    await expect(page.locator('[data-act="hookkind"][data-value="mutate"]')).toBeEnabled();
    for (const kind of ['webhook', 'email', 'function', 'http.call', 'entity.update']) {
      await expect(page.locator(`[data-act="hookkind"][data-value="${kind}"]`)).toHaveCount(0);
    }
    await expect(page.locator('.a-modal')).toContainText('cannot</strong> reach the network'.replace(/<[^>]+>/g, ''));

    // An AFTER hook gets $defs/action's five, three of them disabled with their own refusal.
    await page.click('[data-act="hookpoint"][data-value="afterUpdate"]');
    await page.waitForTimeout(60);
    for (const kind of ['webhook', 'email']) {
      await expect(page.locator(`[data-act="hookkind"][data-value="${kind}"]`)).toBeEnabled();
    }
    for (const kind of ['function', 'http.call', 'entity.update']) {
      await expect(page.locator(`[data-act="hookkind"][data-value="${kind}"]`)).toBeDisabled();
    }
    await expect(page.locator('[data-act="hookkind"][data-value="reject"]')).toHaveCount(0);
    await shot(page, '10-hook-actions');

    console_.assertClean();
  });

  test('works: Automations and Functions quote the warned table and offer nothing', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/automations');
    await reset(page);

    for (const [route, block] of [['#/automations', 'automation'], ['#/functions', 'functions']]) {
      await go(page, route);
      const consequence = CAPABILITIES.warned.find((w) => w.block === block).consequence;
      await expect(page.locator('#app')).toContainText(consequence);
      await expect(page.locator('.a-notyet').first()).toBeVisible();
      // A warned section exists and does nothing; it must not offer a builder.
      await expect(page.locator('[data-act="overlay"][data-kind^="new-"]')).toHaveCount(0);
    }
    await shot(page, '10-not-yet');

    console_.assertClean();
  });

  test('works: the Overview panel is intersected with what the descriptor declares', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);
    await go(page, '#/overview');

    // field-service declares NONE of the five, so the panel must not list five inert blocks.
    await expect(page.locator('#app')).toContainText('This descriptor declares none of them');
    await expect(page.locator('#app')).toContainText('This panel is the only place you will be told');
    await expect(page.locator('#app')).toContainText('A runtime apply writes no warning line at all');
    for (const w of CAPABILITIES.warned) {
      await expect(page.locator('#app')).not.toContainText(w.consequence);
    }

    // Declare one, and it appears — with the server's own sentence.
    await page.evaluate(() => {
      window.__alvoPrototype.wc.working.functions = { nightly: { runtime: 'csx' } };
      window.__alvoPrototype.wc.applied.functions = { nightly: { runtime: 'csx' } };
    });
    await go(page, '#/schema');
    await go(page, '#/overview');
    const functions = CAPABILITIES.warned.find((w) => w.block === 'functions').consequence;
    await expect(page.locator('#app')).toContainText(functions);
    await shot(page, '10-warned-intersected');

    console_.assertClean();
  });

  test('works: nothing on any screen is drawn live and does nothing', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);

    const routes = ['#/overview', '#/schema', '#/schema/work_orders', '#/data/work_orders',
      '#/rules/work_orders', '#/access', '#/history', '#/integrations', '#/settings',
      '#/automations', '#/functions', '#/schema/transfer', '#/notes'];

    for (const route of routes) {
      await go(page, route);
      const dead = await deadControls(page);
      expect(dead, `${route} has a control that is neither wired nor visibly inert`).toEqual([]);
    }

    // The two that need a running instance are disabled with the reason in their title.
    await go(page, '#/schema/work_orders');
    await page.click('[data-act="tab"][data-tab="api"]');
    await page.waitForTimeout(60);
    const openRef = page.locator('button', { hasText: 'Open reference' });
    await expect(openRef).toBeDisabled();
    await expect(openRef).toHaveAttribute('title', /generated OpenAPI document|runtime/);

    console_.assertClean();
  });

  test('works: Settings names no engine, no key issuance and no danger zone', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/settings');
    await reset(page);
    await go(page, '#/settings');

    // No engine is STATED as a fact anywhere — not in a value, not in a badge, not in the shell.
    // Saying why there is none is a different thing, and the page does that.
    const values = await page.locator('dl.p-kv dd').allInnerTexts();
    const badges = await page.locator('.a-badge').allInnerTexts();
    for (const value of [...values, ...badges]) {
      expect(value, 'a value or a badge names a database engine').not.toMatch(/PostgreSQL|SQLite|Postgres|MySQL/i);
    }
    const text = await page.locator('#app').innerText();
    expect(text).toContain('EfAlvoData');
    expect(text).toContain('There is no engine here, and there cannot be');

    // No New key, no Revoke, no Delete project — each would be a button with no possible output.
    await expect(page.locator('[data-kind="new-key"]')).toHaveCount(0);
    await expect(page.locator('button', { hasText: 'Revoke' })).toHaveCount(0);
    await expect(page.locator('button', { hasText: /^Delete/ })).toHaveCount(0);
    expect(text).toContain('Roles are the security-relevant attribute');
    expect(text).toContain('Narrow a key');
    expect(text).toContain('management reach by narrowing its roles');
    await shot(page, '10-settings');

    console_.assertClean();
  });

  test('works: every management route the prototype prints carries /projects/{project}', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/overview');
    await reset(page);

    const unprefixed = new Set(CAPABILITIES.routes.filter((r) => !r.path.includes('{project}')).map((r) => r.path));
    const routes = ['#/overview', '#/schema/transfer', '#/rules/work_orders', '#/settings', '#/notes'];
    for (const route of routes) {
      await go(page, route);
      const text = await page.locator('#app').innerText();
      const printed = [...text.matchAll(/\/management\/[a-z{}\/-]+/g)].map((m) => m[0]);
      for (const path of printed) {
        const tail = path.replace('/management', '');
        const ok = tail.startsWith('/projects') || unprefixed.has(tail);
        expect(ok, `${route} printed ${path}, which is not a route ManagementEndpoints maps`).toBe(true);
      }
    }
    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      for (const route of ['#/integrations', '#/automations', '#/functions', '#/settings']) {
        await open(page, route, cell);
        await expectNoHorizontalScroll(page);
        await expectNothingClipped(page);
      }
      console_.assertClean();
    });
  }
});
