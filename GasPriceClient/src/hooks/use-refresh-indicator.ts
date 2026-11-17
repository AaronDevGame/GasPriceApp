import { useEffect, useRef, useState } from 'react';

/** Keep fast refreshes visible long enough for the opening animation to settle. */
export function useRefreshIndicator(refreshing: boolean) {
  const [held, setHeld] = useState(refreshing);
  const [previous, setPrevious] = useState(refreshing);
  // Adjust on the incoming transition so even a fast completion renders held.
  if (previous !== refreshing) {
    setPrevious(refreshing);
    if (refreshing) setHeld(true);
  }
  const shownAt = useRef<number | null>(null);
  useEffect(() => {
    if (refreshing) {
      shownAt.current ??= Date.now();
      return;
    }
    if (shownAt.current === null) return;
    const remaining = Math.max(0, 900 - (Date.now() - shownAt.current));
    const timer = setTimeout(() => {
      shownAt.current = null;
      setHeld(false);
    }, remaining);
    return () => clearTimeout(timer);
  }, [refreshing]);
  return refreshing || held;
}
