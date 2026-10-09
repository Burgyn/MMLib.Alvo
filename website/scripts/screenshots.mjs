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
const readmeViews = new Set(['overview']);

const password = (await readFile(passwordFile, 'utf8')).trim();
await mkdir(outDir, { recursive: true });
await mkdir(readmeDir, { recursive: true });
const browser = await chromium.launch();
try {
  for (const theme of ['light', 'dark']) await captureTheme(theme);
} finally {
  await browser.close();
}

async function captureTheme(theme) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: theme, deviceScaleFactor: 1 });
  await context.addInitScript((t) => localStorage.setItem('alvo.theme', t), theme);
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
  if (readmeViews.has(name)) await copyFile(file, path.join(readmeDir, `${name}-${theme}.png`));
}

async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(SettleAfterIdleMs);
}
