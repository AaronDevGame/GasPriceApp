import { isAppForeground, subscribeToAppVisibility } from '@/auth/foreground';
import { adviceExpiresAt, adviceIsCurrent, getFuelAdvice, type AdviceFuel, type FuelAdvice } from './advice';

type Snapshot = { result: FuelAdvice | null; loading: boolean; error: boolean };
export const initialAdviceSnapshot: Snapshot = { result: null, loading: true, error: false };
const refreshInterval = 6 * 60 * 60 * 1000;
const retryInterval = 60000;
const maxFailureRetries = 3;

// The server cooldown is per route/IP, including different fuels and companies.
let requestQueue: Promise<unknown> = Promise.resolve();
let nextRequestAt = 0;
function requestAdvice(fuel: AdviceFuel, company: string | null, canRequest: () => boolean) {
  const request = requestQueue.then(async () => {
    if (!canRequest()) return null;
    const delay = nextRequestAt - Date.now();
    if (delay > 0) await new Promise(resolve => setTimeout(resolve, delay));
    // A queued card may have lost focus or the app may have been backgrounded meanwhile.
    if (!canRequest()) return null;
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
  let failures = 0;
  let refreshOnActivation = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let unsubscribeVisibility: (() => void) | undefined;
  const listeners = new Set<() => void>();
  const activeListeners = new Set<() => void>();
  const canRequest = () => activeListeners.size > 0 && isAppForeground();
  const publish = (next: Snapshot) => { snapshot = next; listeners.forEach(listener => listener()); };
  const expiresAt = () => snapshot.result ? adviceExpiresAt(snapshot.result) : Infinity;

  function schedule() {
    clearTimeout(timer);
    if (!listeners.size || !isAppForeground()) return;
    // Inactive visible headers still hide expired advice locally, without fetching.
    // Even a slow refresh must stop showing advice at its effective/expiry boundary.
    const expiry = expiresAt();
    const due = pending || !canRequest() ? (expiry > Date.now() ? expiry : Infinity)
      : Math.min(refreshAt, expiry > Date.now() ? expiry : Infinity);
    if (!Number.isFinite(due)) return;
    timer = setTimeout(() => {
      publish({ ...snapshot });
      if (canRequest()) void reload();
    }, Math.max(1, due - Date.now()));
  }

  function retryAt(now: number) {
    failures++;
    return now + (failures <= maxFailureRetries ? retryInterval * 2 ** (failures - 1) : refreshInterval);
  }

  function reload(): Promise<void> {
    if (pending) return pending;
    if (!canRequest()) return Promise.resolve();
    // Queue before notifying subscribers so overlapping cards/focus/return events share work.
    pending = requestAdvice(fuel, company, canRequest).then(result => {
      if (result === null) { publish({ ...snapshot, loading: false }); return; }
      const now = Date.now();
      if (adviceIsCurrent(result, now)) { failures = 0; refreshAt = now + refreshInterval; }
      else refreshAt = retryAt(now);
      publish({ result, loading: false, error: false });
    }, () => {
      refreshAt = retryAt(Date.now());
      // A failed refresh must not erase advice that is still valid.
      publish({ ...snapshot, loading: false, error: true });
    }).finally(() => { pending = null; schedule(); });
    publish({ ...snapshot, loading: true, error: false });
    schedule();
    return pending;
  }

  function ensureFresh() {
    if (refreshOnActivation || Date.now() >= refreshAt ||
        (snapshot.result && !adviceIsCurrent(snapshot.result, Date.now()) && !failures)) {
      if (canRequest()) refreshOnActivation = false;
      void reload();
    } else schedule();
  }

  return {
    getSnapshot: () => {
      if (!snapshot.result || adviceIsCurrent(snapshot.result, Date.now())) return snapshot;
      // Stable snapshots are required by useSyncExternalStore. Hide stale cached advice
      // on focus/foreground too, even if timers were paused or a refresh is still queued.
      if (expiredSource !== snapshot) {
        expiredSource = snapshot;
        expiredSnapshot = { ...snapshot, result: null };
      }
      return expiredSnapshot;
    },
    reload,
    subscribe(listener: () => void, active = true) {
      listeners.add(listener);
      if (listeners.size === 1) {
        unsubscribeVisibility = subscribeToAppVisibility(visible => {
          clearTimeout(timer);
          if (visible) {
            // Route focus can arrive after the visibility event; remember the refresh
            // until an advice consumer is active instead of requesting in a hidden screen.
            refreshOnActivation = true;
            publish({ ...snapshot });
            if (canRequest()) ensureFresh();
            else schedule();
          }
        });
      }
      if (active) {
        activeListeners.add(listener);
        if (activeListeners.size === 1) ensureFresh();
      } else schedule();
      return () => {
        listeners.delete(listener);
        activeListeners.delete(listener);
        if (!listeners.size) { unsubscribeVisibility?.(); unsubscribeVisibility = undefined; }
        schedule();
      };
    },
  };
}

// Session memory only: a full browser reload always starts with a fresh request.
const stores = new Map<string, ReturnType<typeof createStore>>();
export function fuelAdviceStore(fuel: AdviceFuel, company: string | null) {
  const key = JSON.stringify([fuel, company]);
  let store = stores.get(key);
  if (!store) { store = createStore(fuel, company); stores.set(key, store); }
  return store;
}
