export type TokenLogEntry = { id: number; message: string; time: number };

// Temporary testing diagnostics. Never include tokens, credentials or API responses.
let entries: TokenLogEntry[] = [];
let sequence = 0;
const listeners = new Set<(next: TokenLogEntry[]) => void>();

export function logToken(message: string) {
  entries = [...entries.slice(-2), { id: ++sequence, message, time: Date.now() }];
  for (const listener of listeners) listener(entries);
}

export function subscribeTokenLog(listener: (next: TokenLogEntry[]) => void) {
  listeners.add(listener);
  listener(entries);
  return () => { listeners.delete(listener); };
}
