import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

const port = 4329;
const host = '127.0.0.1';
const baseUrl = `http://${host}:${port}`;

export async function withPreview(fn) {
  const child = spawn('npx', ['astro', 'preview', '--port', String(port), '--host', host, '--ignore-lock'], {
    cwd: new URL('..', import.meta.url),
    stdio: ['ignore', 'pipe', 'pipe'],
    detached: process.platform !== 'win32',
  });
  let output = '';
  child.stdout.on('data', (chunk) => { output += chunk; });
  child.stderr.on('data', (chunk) => { output += chunk; });
  try {
    await waitUntilUp(child, () => output);
    return await fn(baseUrl);
  } finally {
    stop(child);
  }
}

async function waitUntilUp(child, output) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`astro preview exited (${child.exitCode}):\n${output()}`);
    try {
      const response = await fetch(`${baseUrl}/`);
      if (response.status === 200) return;
    } catch {
      // not listening yet
    }
    await delay(250);
  }
  throw new Error(`astro preview did not answer 200 on ${baseUrl}/ within 30 s:\n${output()}`);
}

function stop(child) {
  if (child.exitCode !== null) return;
  try {
    if (child.pid && process.platform !== 'win32') process.kill(-child.pid, 'SIGTERM');
    else child.kill('SIGTERM');
  } catch {
    child.kill('SIGKILL');
  }
}
