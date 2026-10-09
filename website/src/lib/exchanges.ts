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

/** The narrowest line the landing panes hold without scrolling (13px mono in a half-width pane at 1024). */
export const FlowLineWidth = 56;

/**
 * A JSON value in flow style, as the design draws a body: one line when it fits, else one member per line
 * (`{"type":"…",` / ` "detail":"…"}`). Only line breaks change; the values are the captured ones.
 */
export function flowJson(value: unknown): string {
  const line = JSON.stringify(value);
  if (line.length <= FlowLineWidth || value === null || typeof value !== 'object' || Array.isArray(value)) return line;
  const members = Object.entries(value as Record<string, unknown>).map(([key, member]) => `${JSON.stringify(key)}:${JSON.stringify(member)}`);
  return `{${members.join(',\n ')}}`;
}
