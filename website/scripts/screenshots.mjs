import { chromium } from 'playwright';
import { readFile, mkdir, copyFile } from 'node:fs/promises';
import path from 'node:path';

const SettleAfterIdleMs = 800;
const [base, passwordFile, outDir, readmeDir] = process.argv.slice(2);
const views = [
  ['overview', '/admin'],
  ['schema-map', '/admin/schema', (page) => page.getByRole('radio', { name: 'Map', exact: true }).click()],
  ['schema-editor', '/admin/schema/service_orders'],
  ['rules-editor', '/admin/rules/service_orders'],
  ['data-browser', '/admin/data/service_orders'],
  ['history', '/admin/history'],
];
// The rules are edited on the entity's Rules tab (/admin/rules only lists them). The editor checks a rule as
// it is typed and shows a finding only for a rule it refuses, so the crop types a one-letter typo into the
// list rule (never saved) to put the live check in the picture.
const typeRuleTypo = async (page) => {
  await page.getByRole('tab', { name: 'Rules', exact: true }).click();
  await page.getByRole('tab', { name: 'Rules', exact: true, selected: true }).waitFor();
  await page.fill('#rule-list', "'amdin' in @user.roles");
  await page.getByTestId('check-rule-list').first().waitFor();
};
const crops = [
  // 1200 wide, so the dashboard's content region is no wider than the 960px crop (at 1440 it is ~1200 and
  // the crop cut the editor's right edge and its live check).
  { name: 'rules-editor', route: '/admin/schema/service_orders', viewport: { width: 1200, height: 900 }, clip: { width: 960, height: 600 }, prepare: typeRuleTypo, from: 'Who may do what' },
  { name: 'rules-editor-phone', route: '/admin/schema/service_orders', viewport: { width: 390, height: 844 }, clip: { width: 390, height: 520 }, prepare: typeRuleTypo, from: 'Who may do what' },
];
const readmeCrops = new Set(['rules-editor']);

const password = (await readFile(passwordFile, 'utf8')).trim();
await mkdir(outDir, { recursive: true });
await mkdir(readmeDir, { recursive: true });
const browser = await chromium.launch();
try {
  for (const theme of ['light', 'dark']) await captureTheme(theme);
  for (const theme of ['light', 'dark']) {
    for (const crop of crops) await captureCrop(crop, theme);
  }
} finally {
  await browser.close();
}

async function themedContext(theme, viewport, deviceScaleFactor) {
  const context = await browser.newContext({ viewport, colorScheme: theme, deviceScaleFactor });
  await context.addInitScript((t) => localStorage.setItem('alvo.theme', t), theme);
  return context;
}

async function captureTheme(theme) {
  const context = await themedContext(theme, { width: 1440, height: 900 }, 1);
  const page = await context.newPage();
  await signIn(page);
  for (const [name, route, prepare] of views) await capture(page, name, route, prepare, theme);
  await context.close();
}

async function signIn(page) {
  await page.goto(`${base}/admin/sign-in`);
  await page.fill('#email', 'admin@alvo.demo');
  await page.fill('#password', password);
  await Promise.all([page.waitForURL((url) => !url.pathname.endsWith('/sign-in')), page.click('button[type=submit]')]);
  if (new URL(page.url()).pathname.endsWith('/set-password')) {
    throw new Error(`Sign-in landed on ${page.url()}: the bootstrap administrator must set a password first, so there is no dashboard to capture.`);
  }
}

async function capture(page, name, route, prepare, theme) {
  await page.goto(`${base}${route}`);
  await settle(page);
  if (prepare) {
    await prepare(page);
    await settle(page);
  }
  const file = path.join(outDir, `${name}-${theme}.png`);
  await page.screenshot({ path: file });
}

async function captureCrop({ name, route, viewport, clip, prepare, from }, theme) {
  const context = await themedContext(theme, viewport, 2);
  const page = await context.newPage();
  await signIn(page);
  await page.goto(`${base}${route}`);
  await settle(page);
  if (prepare) {
    await prepare(page);
    await settle(page);
  }
  const box = await page.locator('main.a-content').boundingBox();
  if (!box) throw new Error(`${name}: ${route} has no main.a-content to crop to`);
  const top = from ? await scrolledTop(page, from) : box.y;
  const file = path.join(outDir, `${name}-${theme}-2x.png`);
  await page.screenshot({ path: file, clip: { x: box.x, y: top, width: Math.min(clip.width, box.width), height: clip.height } });
  if (readmeCrops.has(name)) await copyFile(file, path.join(readmeDir, `${name}-${theme}-2x.png`));
  await context.close();
}

// A phone viewport has room for one panel: scroll the one the crop is about to the top, under the app header.
async function scrolledTop(page, text) {
  const anchor = page.getByText(text, { exact: true }).first();
  const chrome = await page.evaluate(() => document.querySelector('header')?.getBoundingClientRect().bottom ?? 0);
  await anchor.evaluate((el, offset) => {
    el.style.scrollMarginTop = `${offset}px`;
    el.scrollIntoView({ block: 'start' });
  }, chrome + 40);
  await page.waitForTimeout(SettleAfterIdleMs);
  return (await anchor.boundingBox()).y - 24;
}

async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(SettleAfterIdleMs);
}
