// Renders public/og.png, the 1200×630 Open Graph card every page shares (astro.config.mjs `head`).
// Run by hand after changing the wording: `npm run og:image`. It needs network access for the two fonts.
import { chromium } from 'playwright';

const out = new URL('../public/og.png', import.meta.url);
const html = String.raw`<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@500;600&family=Public+Sans:wght@500;800&display=block">
<style>
  * { box-sizing: border-box; }
  html, body { margin: 0; }
  body { width: 1200px; height: 630px; background: #1e2029; color: #e8eaf0; font-family: 'Public Sans', sans-serif; }
  .card { position: relative; height: 100%; padding: 72px 80px; display: flex; flex-direction: column; }
  .brand { display: flex; align-items: center; gap: 16px; font-weight: 800; font-size: 34px; letter-spacing: -0.02em; }
  .eyebrow { margin-top: 64px; font: 600 20px 'IBM Plex Mono', monospace; letter-spacing: 0.08em; text-transform: uppercase; color: #8b92ab; }
  .eyebrow b { color: #39e991; font-weight: 600; }
  h1 { margin: 22px 0 0; font-weight: 800; font-size: 76px; line-height: 1.04; letter-spacing: -0.04em; max-width: 980px; }
  h1 span { color: #39e991; }
  .foot { margin-top: auto; display: flex; justify-content: space-between; align-items: flex-end; font: 500 22px 'IBM Plex Mono', monospace; color: #9aa0b8; }
  .foot .url { color: #e8eaf0; }
  .rule { position: absolute; left: 0; right: 0; bottom: 0; height: 6px; background: #39e991; }
</style></head>
<body><div class="card">
  <div class="brand">
    <svg width="52" height="52" viewBox="0 0 40 40" aria-hidden="true"><rect x="0.5" y="0.5" width="39" height="39" rx="9" fill="#262833" stroke="#3b3e52"/><g stroke="#39E991" fill="none" stroke-width="3.2" stroke-linecap="round" stroke-linejoin="round"><polyline points="13,13 20,20 13,27"/><line x1="22.5" y1="27" x2="30" y2="27"/></g></svg>
    Alvo
  </div>
  <p class="eyebrow"><b>.NET-native backend-as-a-service</b> · open source · Apache-2.0</p>
  <h1>Write the schema.<br><span>The backend is done.</span></h1>
  <div class="foot">
    <span>one JSON descriptor → REST API · rules · hooks</span>
    <span class="url">alvo.burgyn.online</span>
  </div>
  <div class="rule"></div>
</div></body></html>`;

const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1200, height: 630 }, deviceScaleFactor: 1 });
  await page.setContent(html, { waitUntil: 'networkidle' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: out.pathname, type: 'png' });
  console.log(`wrote ${out.pathname}`);
} finally {
  await browser.close();
}
