import { useEffect, useState } from 'react';

// Re-evaluate expiry while a screen stays open, without reading the clock during render.
export function useNewsClock() {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 60000);
    return () => clearInterval(timer);
  }, []);
  return now;
}
