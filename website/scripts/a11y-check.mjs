import { chromium } from 'playwright';
import { AxeBuilder } from '@axe-core/playwright';
import { withPreview } from './preview-server.mjs';

const pages = ['/', '/start-here/quick-start/', '/guides/access-rules/', '/reference/problem-types/', '/reference/cel-functions/', '/reference/data-api/', '/reference/data-api/operations/ownerslist/'];
const themes = ['light', 'dark'];

await withPreview(async (baseUrl) => {
  const browser = await chromium.launch();
  try {
    for (const path of pages) {
      for (const theme of themes) await audit(browser, baseUrl, path, theme);
    }
  } finally {
    await browser.close();
  }
});

async function audit(browser, baseUrl, path, theme) {
  const context = await browser.newContext();
  const page = await context.newPage();
  try {
    await page.addInitScript((t) => localStorage.setItem('starlight-theme', t), theme);
    const response = await page.goto(baseUrl + path, { waitUntil: 'networkidle' });
    if (response?.status() === 404) {
      if (theme === themes[0]) console.log(`skip ${path}: 404 (its task has not landed yet)`);
      return;
    }
    const actual = await page.evaluate(() => document.documentElement.dataset.theme);
    if (actual !== theme) {
      console.log(`FAIL ${path} [${theme}]: data-theme is "${actual}"`);
      process.exitCode = 1;
      return;
    }
    const { violations } = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    report(path, theme, violations);
  } finally {
    await context.close();
  }
}

function report(path, theme, violations) {
  if (violations.length === 0) {
    console.log(`ok   ${path} [${theme}]: 0 violations`);
    return;
  }
  process.exitCode = 1;
  for (const v of violations) {
    const targets = v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ');
    console.log(`FAIL ${path} [${theme}]: ${v.id} (${v.impact}) ${targets}`);
  }
}
