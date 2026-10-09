import { mkdir, writeFile } from 'node:fs/promises';
import lighthouse from 'lighthouse';
import * as chromeLauncher from 'chrome-launcher';
import { chromium } from 'playwright';
import { withPreview } from './preview-server.mjs';

const pages = ['/', '/guides/access-rules/'];
const categories = ['performance', 'accessibility', 'best-practices'];
const threshold = 0.95;
const outDir = new URL('../.lighthouse/', import.meta.url);

await mkdir(outDir, { recursive: true });
await withPreview(async (baseUrl) => {
  const chrome = await chromeLauncher.launch({
    chromePath: chromium.executablePath(),
    chromeFlags: ['--headless=new', '--no-sandbox'],
  });
  try {
    for (const path of pages) await audit(chrome.port, baseUrl, path);
  } finally {
    await chrome.kill();
  }
});

async function audit(port, baseUrl, path) {
  const url = baseUrl + path;
  const status = (await fetch(url)).status;
  if (status === 404) {
    console.log(`skip ${path}: 404 (its task has not landed yet)`);
    return;
  }
  const result = await lighthouse(url, { port, output: 'json', logLevel: 'error', onlyCategories: categories });
  const name = path === '/' ? 'index' : path.replace(/^\/|\/$/g, '').replaceAll('/', '-');
  await writeFile(new URL(`${name}.json`, outDir), result.report);
  const scores = categories.map((id) => [id, result.lhr.categories[id].score]);
  const line = scores.map(([id, score]) => `${id} ${Math.round(score * 100)}`).join(', ');
  const failing = scores.filter(([, score]) => score < threshold);
  console.log(`${failing.length ? 'FAIL' : 'ok  '} ${path}: ${line}`);
  if (failing.length) {
    process.exitCode = 1;
    printFailingAudits(result.lhr, failing.map(([id]) => id));
  }
}

function printFailingAudits(lhr, ids) {
  for (const id of ids) {
    for (const ref of lhr.categories[id].auditRefs) {
      const audit = lhr.audits[ref.id];
      if (ref.weight > 0 && audit.score !== null && audit.score < 1) console.log(`     ${id}: ${audit.id} ${audit.score} ${audit.displayValue ?? ''}`);
    }
  }
}
