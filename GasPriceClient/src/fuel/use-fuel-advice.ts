import { useCallback, useSyncExternalStore } from 'react';
import { useIsFocused } from 'expo-router';
import type { AdviceFuel } from './advice';
import { fuelAdviceStore, initialAdviceSnapshot } from './advice-store';

export function useFuelAdvice(fuelType: AdviceFuel, oilCompany: string | null, active = true) {
  const store = fuelAdviceStore(fuelType, oilCompany);
  const focused = useIsFocused();
  const subscribe = useCallback((listener: () => void) => store.subscribe(listener, active && focused), [store, active, focused]);
  const snapshot = useSyncExternalStore(subscribe, store.getSnapshot, () => initialAdviceSnapshot);
  const advice = snapshot.result;
  return { advice, loading: snapshot.loading, error: snapshot.error && !advice, reload: store.reload };
}
