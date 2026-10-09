import { chromium } from 'playwright';
import { withPreview } from './preview-server.mjs';

const pages = ['/', '/start-here/quick-start/', '/reference/cel-functions/', '/reference/data-api/operations/ownerslist/'];
const viewports = [[390, 844], [768, 1024], [1024, 768], [1280, 800], [1440, 900]];
const themes = ['light', 'dark'];
const roadmap = '/MMLib.Alvo/project/roadmap/';
const tapScopes = ['header.header', 'footer', '.pagination-links', '.sl-menu-button'];
// Pages whose content is a code-led walkthrough: a missing code frame there is a regression, not a gap.
const framedPages = new Set(['/start-here/quick-start/']);

const failures = [];
const notes = new Set();

await withPreview(async (baseUrl) => {
  const browser = await chromium.launch();
  try {
    for (const path of pages) {
      for (const [width, height] of viewports) {
        for (const theme of themes) await check(browser, baseUrl, path, width, height, theme);
      }
    }
  } finally {
    await browser.close();
  }
});

for (const note of notes) console.log(`note ${note}`);
for (const failure of failures) console.log(`FAIL ${failure}`);
const combinations = pages.length * viewports.length * themes.length;
console.log(`${combinations} combinations, ${failures.length} failure(s)`);
if (failures.length > 0) process.exitCode = 1;

async function check(browser, baseUrl, path, width, height, theme) {
  const context = await browser.newContext({ viewport: { width, height } });
  const page = await context.newPage();
  const fail = (message) => failures.push(`${path} × ${width} × ${theme}: ${message}`);
  try {
    await page.addInitScript((t) => localStorage.setItem('starlight-theme', t), theme);
    await page.goto(baseUrl + path, { waitUntil: 'networkidle' });
    const docs = await page.evaluate(() => document.documentElement.hasAttribute('data-has-sidebar'));
    await noHorizontalScroll(page, fail);
    await headerHeight(page, fail);
    await primaryNav(page, width, fail);
    if (width < 768) await phoneHeader(page, docs, fail);
    if (width === 390) await phoneTargets(page, docs, fail);
    if (width < 800 && !docs) await landingMenu(page, fail);
    if (width === 390 && docs) await phoneDocsLayout(page, path, fail);
    if (width >= 800 && docs) await sidebarPane(page, fail);
    if (width === 1280) await searchShortcut(page, fail);
    await banner(page, docs, fail);
    if (path === '/' && width >= 1024) await heroCode(page, fail);
    await keyboardFocus(page, path, fail);
  } catch (error) {
    fail(`threw ${error.message.split('\n')[0]}`);
  } finally {
    await context.close();
  }
}

async function the(page, selector, fail) {
  const count = await page.locator(selector).count();
  if (count === 0) fail(`selector "${selector}" matches nothing`);
  return count > 0;
}

async function noHorizontalScroll(page, fail) {
  const { scrollWidth, innerWidth } = await page.evaluate(() => ({
    scrollWidth: document.documentElement.scrollWidth,
    innerWidth: window.innerWidth,
  }));
  if (scrollWidth > innerWidth) fail(`horizontal page scroll: scrollWidth ${scrollWidth} > innerWidth ${innerWidth}`);
}

async function headerHeight(page, fail) {
  if (!(await the(page, 'header.header', fail))) return;
  const box = await page.locator('header.header').boundingBox();
  if (box.height > 56.5) fail(`header is ${box.height}px tall (max 56.5)`);
}

async function primaryNav(page, width, fail) {
  if (!(await the(page, 'nav.alvo-primary-nav', fail))) return;
  const visible = await page.locator('nav.alvo-primary-nav').isVisible();
  if (width >= 1024 && !visible) fail('the primary nav is hidden at ≥ 1024px');
  if (width < 1024 && visible) fail('the primary nav is visible below 1024px');
}

async function phoneHeader(page, docs, fail) {
  const buttons = ['site-search button[data-open-modal]'];
  if (docs) buttons.push('.sl-menu-button');
  for (const selector of buttons) {
    if (!(await the(page, selector, fail))) continue;
    const box = await page.locator(selector).first().boundingBox();
    if (!box || box.width < 44 || box.height < 44) fail(`${selector} is ${box?.width}×${box?.height} (min 44×44)`);
  }
}

async function phoneTargets(page, docs, fail) {
  for (const scope of tapScopes) {
    if (scope === '.sl-menu-button' && !docs) continue;
    if (await the(page, scope, fail)) await tallEnough(page, scope, fail);
  }
  if (!docs) return;
  await page.locator('.sl-menu-button').click();
  await page.waitForFunction(() => document.querySelector('#starlight__sidebar')?.matches(':popover-open'));
  await tallEnough(page, '#starlight__sidebar', fail);
  await page.locator('.sl-menu-button').click();
}

// The landing has no sidebar, so no Starlight drawer: its menu sheet must reach the docs, the roadmap,
// GitHub and the theme control (R-T4-3), each a 44px target.
async function landingMenu(page, fail) {
  if (!(await the(page, '.alvo-sheet-toggle', fail))) return;
  const toggle = await page.locator('.alvo-sheet-toggle').boundingBox();
  if (!toggle || toggle.width < 44 || toggle.height < 44) fail(`the menu button is ${toggle?.width}×${toggle?.height} (min 44×44)`);
  await page.locator('.alvo-sheet-toggle').click();
  await page.waitForFunction(() => document.querySelector('#alvo-sheet')?.matches(':popover-open'));
  const wanted = [
    ['the docs', '#alvo-sheet a[href="/MMLib.Alvo/start-here/why-alvo/"]'],
    ['the roadmap', `#alvo-sheet a[href="${roadmap}"]`],
    ['GitHub', '#alvo-sheet a[href="https://github.com/Burgyn/MMLib.Alvo"]'],
    ['the theme control', '#alvo-sheet starlight-theme-select select'],
  ];
  for (const [what, selector] of wanted) {
    if (!(await page.locator(selector).first().isVisible())) fail(`the menu sheet does not show ${what} (${selector})`);
  }
  await tallEnough(page, '#alvo-sheet', fail);
  await page.keyboard.press('Escape');
}

async function tallEnough(page, scope, fail) {
  const small = await page.evaluate((s) => {
    const roots = [...document.querySelectorAll(s)];
    const targets = roots.flatMap((root) => [root, ...root.querySelectorAll('a, button, select, summary')])
      .filter((el) => el.matches('a, button, select, summary'));
    return targets
      .map((el) => ({ el, box: el.getBoundingClientRect() }))
      .filter(({ el, box }) => box.width > 0 && box.height > 0 && el.checkVisibility({ visibilityProperty: true }))
      .filter(({ box }) => box.height < 44)
      .map(({ el, box }) => `${el.tagName.toLowerCase()} "${(el.textContent || el.getAttribute('aria-label') || '').trim().slice(0, 30)}" ${Math.round(box.height)}px`);
  }, scope);
  for (const entry of small) fail(`in ${scope}: ${entry} tall (min 44)`);
}

async function phoneDocsLayout(page, path, fail) {
  await the(page, 'mobile-starlight-toc', fail);
  if (await the(page, 'h1', fail)) {
    const top = (await page.locator('h1').first().boundingBox()).y;
    if (top >= 240) fail(`the H1 starts at ${top}px (must be < 240: the sidebar is above the article?)`);
  }
  if ((await page.locator('.expressive-code').count()) === 0) {
    if (framedPages.has(path)) fail('no code frame, so the copy-button check has nothing to check');
    else notes.add(`${path}: no code frame on this page, the copy-button check does not apply`);
    return;
  }
  const copy = page.locator('.expressive-code .copy button').filter({ visible: true }).first();
  const box = await copy.boundingBox();
  const inside = box && box.x >= 0 && box.x + box.width <= 390;
  if (!inside) fail(`the first visible code frame's copy button lies outside the viewport (${JSON.stringify(box)})`);
}

async function sidebarPane(page, fail) {
  if (!(await the(page, '#starlight__sidebar', fail))) return;
  const pane = await page.evaluate(() => {
    const el = document.querySelector('#starlight__sidebar');
    const style = getComputedStyle(el);
    const box = el.getBoundingClientRect();
    const header = document.querySelector('header.header').getBoundingClientRect();
    return {
      position: style.position, overflowY: style.overflowY, border: parseFloat(style.borderInlineEndWidth),
      top: box.top, bottom: box.bottom, headerBottom: header.bottom, innerHeight: window.innerHeight,
    };
  });
  if (!['fixed', 'sticky'].includes(pane.position)) fail(`the sidebar pane is position: ${pane.position}`);
  if (pane.overflowY !== 'auto') fail(`the sidebar pane does not scroll on its own (overflow-y: ${pane.overflowY})`);
  if (Math.abs(pane.top - pane.headerBottom) > 1) fail(`the sidebar pane starts at ${pane.top}, not under the header (${pane.headerBottom})`);
  if (Math.abs(pane.bottom - pane.innerHeight) > 1) fail(`the sidebar pane ends at ${pane.bottom}, not at the viewport bottom`);
  if (!(pane.border >= 1)) fail('the sidebar pane has no full-height border');
}

async function searchShortcut(page, fail) {
  if (!(await the(page, 'site-search button[data-open-modal]', fail))) return;
  const text = await page.locator('site-search button[data-open-modal]').innerText();
  if (!/Ctrl\s*K|⌘\s*K/.test(text)) fail(`the search button reads "${text.replace(/\s+/g, ' ')}", not Ctrl K / ⌘K`);
}

async function banner(page, docs, fail) {
  const count = await page.locator('.alvo-banner').count();
  if (!docs) {
    if (count > 0) fail('the banner renders on the landing page (docs pages only)');
    return;
  }
  if (!(await the(page, '.alvo-banner', fail))) return;
  const links = await page.locator(`.alvo-banner a[href="${roadmap}"]`).count();
  if (links === 0) fail(`the banner does not link ${roadmap}`);
}

async function heroCode(page, fail) {
  if ((await page.locator('.alvo-hero').count()) === 0) return;
  if (!(await the(page, '.alvo-hero pre', fail))) return;
  const wide = await page.evaluate(() => [...document.querySelectorAll('.alvo-hero pre')]
    .filter((pre) => pre.scrollWidth > pre.clientWidth)
    .map((pre) => `${pre.scrollWidth}>${pre.clientWidth}`));
  for (const entry of wide) fail(`a hero code block scrolls sideways (${entry})`);
}

async function keyboardFocus(page, path, fail) {
  const container = await page.evaluate((p) => {
    const focusable = 'a[href], button:not([disabled]), select, summary, [tabindex]:not([tabindex="-1"])';
    if (p === '/' && document.querySelector('.alvo-hero')) return '.alvo-hero';
    if (document.querySelector(`.sl-markdown-content :is(${focusable})`)) return '.sl-markdown-content';
    return 'main';
  }, path);
  await page.waitForFunction(() => !document.querySelector('.main-frame[inert]'));
  await page.evaluate(() => document.activeElement?.blur());
  if (container === 'main') fail('no focusable content in the article, so the keyboard check cannot reach it');
  for (let i = 0; i < 80; i++) {
    await page.keyboard.press('Tab');
    const inside = await page.evaluate((c) => Boolean(document.activeElement?.closest(c)), container);
    if (inside) return assertRing(page, fail);
  }
  fail(`Tab never reached ${container}`);
}

async function assertRing(page, fail) {
  const ring = await page.evaluate(() => {
    const style = getComputedStyle(document.activeElement);
    return { outline: style.outlineStyle, shadow: style.boxShadow, tag: document.activeElement.tagName };
  });
  if (ring.outline === 'none') fail(`the focused ${ring.tag} has outline-style: none`);
  else if (ring.shadow === 'none') fail(`the focused ${ring.tag} shows no focus ring (box-shadow: none)`);
}
