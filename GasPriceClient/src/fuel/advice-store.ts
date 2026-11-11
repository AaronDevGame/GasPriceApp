import { subscribeToForeground } from '@/auth/foreground';
import { adviceIsCurrent, getFuelAdvice, type AdviceFuel, type FuelAdvice } from './advice';

type Snapshot = { result: FuelAdvice | null; loading: boolean; error: boolean };
export const initialAdviceSnapshot: Snapshot = { result: null, loading: true, error: false };
const refreshInterval = 300000;
const retryInterval = 60000;

// The server cooldown is per route/IP, including different fuels and companies.
let requestQueue: Promise<unknown> = Promise.resolve();
let nextRequestAt = 0;
function requestAdvice(fuel: AdviceFuel, company: string | null) {
  const request = requestQueue.then(async () => {
    const delay = nextRequestAt - Date.now();
    if (delay > 0) await new Promise(resolve => setTimeout(resolve, delay));
    try { return await getFuelAdvice(fuel, company); }
    finally { nextRequestAt = Date.now() + 1100; }
  });
  requestQueue = request.catch(() => undefined);
  return request;
}

function createStore(fuel: AdviceFuel, company: string | null) {
  let snapshot = initialAdviceSnapshot;
  let expiredSource: Snapshot | undefined;
  let expiredSnapshot: Snapshot;
  let pending: Promise<void> | null = null;
  let refreshAt = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let unsubscribeForeground: (() => void) | undefined;
  const listeners = new Set<() => void>();
  const publish = (next: Snapshot) => { snapshot = next; listeners.forEach(listener => listener()); };
  const expiresAt = () => snapshot.result?.validUntilUtc ? Date.parse(snapshot.result.validUntilUtc) : Infinity;

  function schedule() {
    clearTimeout(timer);
    if (!listeners.size) return;
    // Even a slow refresh must stop showing advice at its effective/expiry boundary.
    const expiry = expiresAt();
    const due = pending ? (expiry > Date.now() ? expiry : Infinity)
      : Math.min(refreshAt, expiry > Date.now() ? expiry : Infinity);
    if (!Number.isFinite(due)) return;
    timer = setTimeout(() => {
      publish({ ...snapshot });
      void reload();
    }, Math.max(1, due - Date.now()));
  }

  function reload(): Promise<void> {
    if (pending) return pending;
    // Queue the request before notifying subscribers, so every consumer shares it.
    pending = requestAdvice(fuel, company).then(result => {
      const now = Date.now();
      refreshAt = adviceIsCurrent(result, now)
        ? Math.min(now + refreshInterval, result.validUntilUtc ? Date.parse(result.validUntilUtc) : Infinity)
        : now + retryInterval;
      publish({ result, loading: false, error: false });
    }, () => {
      const now = Date.now();
      refreshAt = snapshot.result && adviceIsCurrent(snapshot.result, now)
        ? Math.min(now + retryInterval, expiresAt()) : now + retryInterval;
      // A failed refresh must not erase advice that is still valid.
      publish({ ...snapshot, loading: false, error: true });
    }).finally(() => { pending = null; schedule(); });
    publish({ ...snapshot, loading: true, error: false });
    schedule();
    return pending;
  }

  function ensureFresh() {
    if (Date.now() >= refreshAt) void reload();
    else schedule();
  }

  return {
    getSnapshot: () => {
      if (!snapshot.result || adviceIsCurrent(snapshot.result, Date.now())) return snapshot;
      // Stable snapshots are required by useSyncExternalStore. Also hide expired
      // cache entries immediately on remount, before subscription effects run.
      if (expiredSource !== snapshot) {
        expiredSource = snapshot;
        expiredSnapshot = { ...snapshot, result: null };
      }
      return expiredSnapshot;
    },
    reload,
    subscribe(listener: () => void) {
      listeners.add(listener);
      if (listeners.size === 1) {
        unsubscribeForeground = subscribeToForeground(ensureFresh);
        ensureFresh();
      }
      return () => {
        listeners.delete(listener);
        if (!listeners.size) { clearTimeout(timer); unsubscribeForeground?.(); unsubscribeForeground = undefined; }
      };
    },
  };
}

const stores = new Map<string, ReturnType<typeof createStore>>();
export function fuelAdviceStore(fuel: AdviceFuel, company: string | null) {
  const key = JSON.stringify([fuel, company]);
  let store = stores.get(key);
  if (!store) { store = createStore(fuel, company); stores.set(key, store); }
  return store;
}
