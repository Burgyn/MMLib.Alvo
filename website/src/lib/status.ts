export type StatusTone = 'ok' | 'neutral' | 'warn' | 'danger';

export function statusTone(status: number): StatusTone {
  if (status >= 500) return 'danger';
  if (status >= 400) return 'warn';
  if (status >= 200 && status < 300) return 'ok';
  return 'neutral';
}
