import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeToForeground } from '@/auth/foreground';
import { adviceIsCurrent, getFuelAdvice, type AdviceFuel, type FuelAdvice } from './advice';

export function useFuelAdvice(fuelType: AdviceFuel, oilCompany: string | null) {
  const [result, setResult] = useState<FuelAdvice | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const generation = useRef(0);
  const reload = useCallback(async () => {
    const current = ++generation.current;
    setNow(Date.now()); setLoading(true); setError(false);
    try {
      const advice = await getFuelAdvice(fuelType, oilCompany);
      if (generation.current === current) setResult(advice);
    } catch {
      if (generation.current === current) { setResult(null); setError(true); }
    } finally {
      if (generation.current === current) { setNow(Date.now()); setLoading(false); }
    }
  }, [fuelType, oilCompany]);

  useEffect(() => {
    const requests = generation;
    const current = ++requests.current;
    // Defer initial work so cleanup can cancel a superseded selection before it starts.
    void Promise.resolve().then(() => { if (requests.current === current) void reload(); });
    const unsubscribe = subscribeToForeground(() => void reload());
    return () => { requests.current++; unsubscribe(); };
  }, [reload]);

  const matches = result?.fuelType === fuelType && result.oilCompany === oilCompany;
  const advice = matches && result && adviceIsCurrent(result, now) ? result : null;
  useEffect(() => {
    if (loading) return;
    // Refresh exactly at a time boundary, and at least every five minutes for new announcements.
    // Failed or already-expired responses retry after a minute instead of looping immediately.
    const until = matches && result?.validUntilUtc ? Date.parse(result.validUntilUtc) - Date.now() : Infinity;
    const delay = error || until <= 0 ? 60000 : Math.min(Number.isFinite(until) ? until : 300000, 300000);
    const timer = setTimeout(() => void reload(), Math.max(1000, delay));
    return () => clearTimeout(timer);
  }, [result, matches, loading, error, reload]);

  return { advice, loading: loading || (!matches && !error), error, reload };
}
