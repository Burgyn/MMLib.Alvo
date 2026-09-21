import { defineConfig } from '@playwright/test';

/* The prototype has no build step, so the "server" is a static file server rooted at the
   REPOSITORY ROOT — index.html links src/MMLib.Alvo.Admin/wwwroot/alvo.css directly, so there is
   exactly one copy of the design system and nothing to keep in sync. */
const PORT = Number(process.env.ALVO_PROTOTYPE_PORT ?? 8099);
const ROOT = new URL('../../../../', import.meta.url).pathname;

export default defineConfig({
  testDir: '.',
  outputDir: './.artifacts',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],
  timeout: 30_000,
  expect: { timeout: 7_000 },
  use: {
    baseURL: `http://127.0.0.1:${PORT}/docs/design/f5-admin/`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `python3 -m http.server ${PORT} --bind 127.0.0.1 --directory ${JSON.stringify(ROOT)}`,
    url: `http://127.0.0.1:${PORT}/docs/design/f5-admin/index.html`,
    reuseExistingServer: !process.env.CI,
    stdout: 'ignore',
    stderr: 'ignore',
  },
});
