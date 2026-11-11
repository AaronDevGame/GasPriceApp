import { useSyncExternalStore } from 'react';
import type { AdviceFuel } from './advice';
import { fuelAdviceStore, initialAdviceSnapshot } from './advice-store';

export function useFuelAdvice(fuelType: AdviceFuel, oilCompany: string | null) {
  const store = fuelAdviceStore(fuelType, oilCompany);
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot, () => initialAdviceSnapshot);
  const advice = snapshot.result;
  return { advice, loading: snapshot.loading, error: snapshot.error && !advice, reload: store.reload };
}
