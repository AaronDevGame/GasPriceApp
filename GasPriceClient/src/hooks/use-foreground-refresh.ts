import { useEffect, useRef } from 'react';

import { subscribeToForeground } from '@/auth/foreground';

/** Keep one subscription while reading the current selection and loading state. */
export function useForegroundRefresh(onReturn: () => void) {
  const latest = useRef(onReturn);
  useEffect(() => { latest.current = onReturn; }, [onReturn]);
  useEffect(() => subscribeToForeground(() => latest.current()), []);
}
