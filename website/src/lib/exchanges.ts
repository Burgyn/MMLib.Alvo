// Read access to the exchanges DocsGen captured at build time (src/generated/exchanges, gitignored).
export interface CapturedStep {
  curl: string;
  httpRequest: string;
  status: number;
  reason: string;
  httpResponse: string;
  responseBody: Record<string, unknown> | null;
}
export interface CapturedExchange { name: string; steps: CapturedStep[] }

const all = import.meta.glob<CapturedExchange>('../generated/exchanges/**/*.json', { eager: true, import: 'default' });

export function capturedExchange(name: string): CapturedExchange {
  const exchange = all[`../generated/exchanges/${name}.json`];
  if (!exchange) throw new Error(`Exchange: no captured exchange "${name}" (run npm run gen)`);
  return exchange;
}

export function capturedStep(name: string, step: number): CapturedStep {
  const captured = capturedExchange(name).steps[step];
  if (!captured) throw new Error(`the captured exchange "${name}" has no step ${step}`);
  return captured;
}

/** The request line of a captured step without its protocol: `GET /api/tickets?priority=eq.high`. */
export function requestLine(name: string, step: number): { method: string; target: string } {
  const [method, target] = capturedStep(name, step).httpRequest.split('\n')[0].split(' ');
  return { method, target };
}
