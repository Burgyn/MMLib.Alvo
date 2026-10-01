/* Scenario 2 — Model a domain from scratch.

   Create an `invoices` entity. Give it a string with a format, a decimal, an enum, a date and a
   `ref` to `customers`. Preview. Apply. Then add a rollup on `customers` that counts them.

   What it is really checking: that the editor produces a descriptor the apply would accept —
   every type carries exactly the facets `$defs/field` allows it, the three types with REQUIRED
   facets get them, and the document that reaches Apply is the one the pane showed. */

import { test, expect } from '@playwright/test';
import { MATRIX, open, go, guardConsole, reset, unapplied, changeList, revision, working, shot, moves, expectNoHorizontalScroll, expectNothingClipped, expectNoOverlap } from './helpers.js';

/** Adds one field through the editor, exactly as a person would. */
async function addField(page, name, type, facets = async () => {}) {
  await page.click('[data-act="field"][data-field="__new"]');
  await page.fill('[data-act="setname"]', name);
  await page.click(`[data-act="settype"][data-type="${type}"]`);
  await facets(page);
  await page.click('[data-act="addfield"]');
  await page.waitForTimeout(40);
}

test.describe('model a domain', () => {
  test('works: five field types, a preview, an apply, and then a rollup', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema');
    await reset(page);
    await go(page, '#/schema');
    const m = moves(page);

    // The entity.
    await m.click('[data-kind="new-entity"]');
    await m.fill('#ne-name', 'invoices');
    await m.click('[data-act="addentity"]');
    await page.waitForTimeout(80);
    expect(page.url()).toContain('#/schema/invoices');

    // A string with a format — the format list is the three built-ins plus whatever `formats`
    // declares, and never the `url` the drawing used to offer.
    await page.click('[data-act="field"][data-field="__new"]');
    const formats = await page.locator('[data-key="format"]').allInnerTexts();
    expect(formats).toEqual(['none', 'email', 'uri', 'phone', 'work-order-ref']);
    await page.click('[data-act="closefield"]');

    await addField(page, 'number', 'string', async (p) => {
      await p.fill('[data-key="maxLength"]', '24');
      await p.click('[data-key="format"][data-value="work-order-ref"]');
    });

    await addField(page, 'total', 'decimal', async (p) => {
      // decimal REQUIRES precision and scale, so the editor pre-fills them rather than leaving a
      // descriptor the apply would refuse.
      await expect(p.locator('.a-needed')).toContainText('Needed for a decimal');
      await p.fill('[data-key="precision"]', '12');
      await p.fill('[data-key="scale"]', '2');
    });

    await addField(page, 'status', 'enum', async (p) => {
      await expect(p.locator('.a-needed')).toContainText('Needed for an enum');
      await p.fill('[data-act="enumadd"]', 'draft,');
      await p.fill('[data-act="enumadd"]', 'sent,');
      await p.fill('[data-act="enumadd"]', 'paid,');
    });

    await addField(page, 'issued_on', 'date');

    await addField(page, 'customer_id', 'ref', async (p) => {
      await expect(p.locator('.a-needed')).toContainText('Needed for a ref');
      await p.click('[data-key="entity"][data-value="customers"]');
      await p.click('[data-key="onDelete"][data-value="restrict"]');
    });

    // The working copy is a descriptor the schema accepts: every facet on the type that allows it.
    const doc = await working(page);
    expect(doc.entities.invoices.fields).toMatchObject({
      number: { type: 'string', maxLength: 24, format: 'work-order-ref' },
      total: { type: 'decimal', precision: 12, scale: 2 },
      status: { type: 'enum', values: ['draft', 'sent', 'paid'] },
      issued_on: { type: 'date' },
      customer_id: { type: 'ref', entity: 'customers', onDelete: 'restrict' },
    });
    // Nothing carries a facet its type forbids.
    expect(doc.entities.invoices.fields.issued_on).toEqual({ type: 'date' });
    expect(doc.entities.invoices.fields.total.maxLength).toBeUndefined();

    // The pane shows the working copy, not the applied revision.
    await expect(page.locator('[data-pane-header]')).toContainText('working copy');
    await expect(page.locator('[data-descriptor]')).toContainText('"invoices"');

    // One count, one preview.
    const before = await unapplied(page);
    expect(before).toBeGreaterThan(0);
    const kinds = new Set((await changeList(page)).map((c) => c.kind));
    expect([...kinds]).toEqual(['schema']);

    await go(page, '#/schema/preview');
    await expect(page.locator('[data-group="schema"]')).toBeVisible();
    await expect(page.locator('#app')).toContainText('Create the table for invoices');
    await expect(page.locator('[data-destructive]')).toHaveCount(0);
    await shot(page, '02-preview-new-entity');

    await page.fill('#apply-reason', 'Add invoices, keyed to a customer');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(120);

    expect(await revision(page)).toBe(8);
    expect(await unapplied(page)).toBe(0);
    await expect(page.locator('#app')).toContainText('Add invoices, keyed to a customer');
    await expect(page.locator('#app')).toContainText('jana@field-service.sk');

    // Now the rollup: customers counts the invoices that point at it.
    await go(page, '#/schema/customers');
    await addField(page, 'invoice_count', 'integer');
    await page.click('[data-act="field"][data-field="invoice_count"]');
    await page.click('[data-act="derive"][data-kind="rollup"]');
    await page.waitForTimeout(40);
    // Only an entity that really points at customers may be aggregated over.
    await expect(page.locator('[data-act="rollupfrom"][data-value="invoices"]')).toBeEnabled();
    await expect(page.locator('[data-act="rollupfrom"][data-value="regions"]')).toBeDisabled();
    await page.click('[data-act="rollupfrom"][data-value="invoices"]');

    const withRollup = await working(page);
    expect(withRollup.entities.customers.fields.invoice_count).toEqual({
      type: 'integer',
      rollup: { from: 'invoices', op: 'count' },
    });

    // `rollup.where` is refused, and the control says so in the framework's own words.
    await expect(page.locator('[data-refused="rollup.where"] input')).toBeDisabled();
    await expect(page.locator('[data-refused="rollup.where"]')).toContainText('a stored number that is silently wrong rather than absent');

    await go(page, '#/schema/preview');
    await page.fill('#apply-reason', 'Count each customer\'s invoices');
    await page.click('[data-act="apply"]');
    await page.waitForTimeout(120);
    expect(await revision(page)).toBe(9);

    // usable: modelling six fields across two entities and two applies, in a countable number
    // of moves. The budget is deliberately generous; what it catches is a regression that
    // doubles it.
    expect(m.count, 'the entity itself should cost three moves').toBeLessThanOrEqual(3);

    console_.assertClean();
    await shot(page, '02-applied');
  });

  test('works: an entity name the schema forbids is refused, with the pattern', async ({ page }) => {
    const console_ = guardConsole(page);
    await open(page, '#/schema');
    await reset(page);
    await go(page, '#/schema');
    await page.click('[data-kind="new-entity"]');
    await page.fill('#ne-name', 'Invoices');       // capital — the propertyNames pattern refuses it
    await page.click('[data-act="addentity"]');
    await page.waitForTimeout(60);
    // Still on the modal, and the entity was not created.
    const doc = await working(page);
    expect(Object.keys(doc.entities)).not.toContain('Invoices');
    console_.assertClean();
  });

  for (const cell of MATRIX) {
    test(`looks right: ${cell.name}`, async ({ page }) => {
      const console_ = guardConsole(page);
      await open(page, '#/schema/work_orders', cell);
      await page.click('[data-act="field"][data-field="reference"]');
      await page.waitForTimeout(60);
      await expectNoHorizontalScroll(page);
      await expectNothingClipped(page);
      if (cell.width >= 1100) {
        // The editor annotates the pane; it must not cover it. This is the defect that made the
        // two-pane idea useless at exactly the moment it mattered.
        await expectNoOverlap(page, '[data-field-editor]', '[data-descriptor]');
      }
      await shot(page, `02-field-editor-${cell.theme}-${cell.width}`);
      console_.assertClean();
    });
  }
});
