import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '@/auth/auth-provider';
import { errorMessage } from '@/auth/http';
import { getHistory, getPrices, refreshLocation, type Area, type Coordinates, type Feed, type PricePoint } from './api';
import { requestLocation } from './location';
import { locationErrorMessage } from './location-error';

export function useFuelPrices() {
  const { session, loading: authLoading, error: authError, restore, logout } = useAuth();
  const [feed, setFeed] = useState<Feed | null>(null);
  const [updates, setUpdates] = useState<PricePoint[]>([]);
  const [historyError, setHistoryError] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [locationNote, setLocationNote] = useState('Use your location or choose a city or province below.');
  const area = useRef<Area | undefined>(undefined);
  const coordinates = useRef<Coordinates | undefined>(undefined);
  const version = useRef(0);
  const running = useRef(false);
  const prompted = useRef(false);
  const authenticated = session?.state === 'authenticated';

  const publish = useCallback((next: Feed, ticket: number) => {
    if (ticket !== version.current) return;
    setFeed(next);
    setUpdates([]);
    setHistoryError(false);
    const first = next.items[0];
    if (first) void getHistory(first).then(
      points => { if (ticket === version.current) setUpdates(points); },
      () => { if (ticket === version.current) setHistoryError(true); },
    );
  }, []);

  const locate = useCallback(async (ticket: number) => {
    setLocationNote('Waiting for location permission…');
    let position: Coordinates | null;
    try { position = await requestLocation(); } catch (failure) {
      if (ticket === version.current) setLocationNote(locationErrorMessage(failure));
      return;
    }
    if (ticket !== version.current) return;
    if (!position) {
      setLocationNote('Location access is blocked. Check your browser and device location permissions, or choose a city or province below.');
      return;
    }
    coordinates.current = position;
    setLocationNote('Location received. Updating prices near you…');
    try {
      const next = await refreshLocation(position);
      if (ticket !== version.current) return;
      area.current = next.area;
      publish(next.feed, ticket);
      setLocationNote(`Prices near ${next.area.resolved_area}.`);
    } catch (failure) {
      if (ticket === version.current) {
        setError(errorMessage(failure));
        setLocationNote('Your location was received, but local prices could not be updated. Try refreshing prices.');
      }
    }
  }, [publish]);

  const run = useCallback(async (operation: (ticket: number) => Promise<void>) => {
    if (running.current) return;
    running.current = true;
    const ticket = ++version.current;
    setBusy(true);
    setError(null);
    try { await operation(ticket); } catch (failure) {
      if (ticket === version.current) {
        setError(errorMessage(failure));
        setLocationNote('Saved prices are still available. Try refreshing or choose an area.');
      }
    } finally {
      if (ticket === version.current) { running.current = false; setBusy(false); }
    }
  }, []);

  const cancel = useCallback(() => {
    version.current++;
    running.current = false;
    setBusy(false);
  }, []);

  useEffect(() => {
    if (!authenticated) return;
    void run(async ticket => {
      if (prompted.current) {
        if (coordinates.current) publish((await refreshLocation(coordinates.current)).feed, ticket);
        else publish(await getPrices(area.current), ticket);
        return;
      }
      publish(await getPrices(), ticket); // First display always has no query parameters.
      if (ticket !== version.current || prompted.current) return;
      prompted.current = true;
      // Give the saved-price screen a chance to render before the OS/browser prompt.
      await new Promise(resolve => setTimeout(resolve, 300));
      if (ticket === version.current) await locate(ticket);
    });
    return cancel;
  }, [authenticated, run, publish, locate, cancel]);

  const refresh = () => {
    if (!authenticated || authError) { void restore(); return; }
    void run(async ticket => {
      if (coordinates.current) {
        const next = await refreshLocation(coordinates.current);
        publish(next.feed, ticket);
      } else publish(await getPrices(area.current), ticket);
    });
  };
  const selectArea = (selected: Area) => run(async ticket => {
    const next = await getPrices(selected);
    if (ticket !== version.current) return;
    area.current = selected;
    coordinates.current = undefined;
    publish(next, ticket);
    setLocationNote(next.localAreaStatus === 'not_cached'
      ? 'No saved prices for that area yet. Showing other available areas.'
      : 'Showing your selected area first.');
  });
  const useLocation = () => run(locate);
  const signOut = async () => {
    cancel();
    await logout();
  };
  return { feed, updates, historyError, busy: busy || authLoading, error: authError ?? error,
    locationNote, authenticated, refresh, selectArea, useLocation, signOut };
}
