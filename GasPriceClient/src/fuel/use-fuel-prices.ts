import { useCallback, useEffect, useRef, useState } from 'react';
import { useAuth } from '@/auth/auth-provider';
import { errorMessage } from '@/auth/http';
import { getFeaturedPrices, getHistory, refreshLocation, type Coordinates, type DoeFuelPriceFeed, type FeaturedFuelPriceFeed, type FeedItem, type PricePoint } from './api';
import { requestLocation } from './location';
import { locationErrorMessage } from './location-error';

export function useFuelPrices() {
  const { session, loading: authLoading, error: authError, restore, logout } = useAuth();
  const [featured, setFeatured] = useState<FeaturedFuelPriceFeed | null>(null);
  const [featuredError, setFeaturedError] = useState(false);
  const [localItem, setLocalItem] = useState<FeedItem | null>(null);
  const [doePrices, setDoePrices] = useState<DoeFuelPriceFeed | null>(null);
  const [updates, setUpdates] = useState<PricePoint[]>([]);
  const [historyError, setHistoryError] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [locationNote, setLocationNote] = useState('Use your location to see prices near you.');
  const coordinates = useRef<Coordinates | undefined>(undefined);
  const version = useRef(0);
  const running = useRef(false);
  const prompted = useRef(false);
  const authenticated = session?.state === 'authenticated';

  const publishLocal = useCallback((next: FeedItem | null, ticket: number) => {
    if (ticket !== version.current) return;
    setLocalItem(next);
    setUpdates([]);
    setHistoryError(false);
    if (next) void getHistory(next).then(
      points => { if (ticket === version.current) setUpdates(points); },
      () => { if (ticket === version.current) setHistoryError(true); },
    );
  }, []);

  const loadFeatured = useCallback(async (ticket: number) => {
    try {
      const next = await getFeaturedPrices();
      if (ticket === version.current) { setFeatured(next); setFeaturedError(false); }
    } catch {
      if (ticket === version.current) setFeaturedError(true);
    }
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
      setLocationNote('Location access is blocked. Check your browser and device location permissions to use your location.');
      return;
    }
    coordinates.current = position;
    setLocationNote('Location received. Updating prices near you…');
    try {
      const next = await refreshLocation(position);
      if (ticket !== version.current) return;
      const local = next.feed.items.find(item => item.isLocal) ?? null;
      publishLocal(local, ticket);
      setDoePrices(next.doePrices);
      setLocationNote(local ? `Prices near ${next.area.resolved_area}.` :
        'No saved prices are available near your location yet.');
    } catch (failure) {
      if (ticket === version.current) {
        setError(errorMessage(failure));
        setLocationNote('Your location was received, but local prices could not be updated. Try refreshing prices.');
      }
    }
  }, [publishLocal]);

  const run = useCallback(async (operation: (ticket: number) => Promise<void>) => {
    if (running.current) return;
    running.current = true;
    const ticket = ++version.current;
    setBusy(true);
    setError(null);
    try { await operation(ticket); } catch (failure) {
      if (ticket === version.current) {
        setError(errorMessage(failure));
        setLocationNote('Featured prices are still available. Try refreshing.');
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
      await loadFeatured(ticket);
      if (ticket !== version.current || prompted.current) return;
      prompted.current = true;
      // Give the saved-price screen a chance to render before the OS/browser prompt.
      await new Promise(resolve => setTimeout(resolve, 300));
      if (ticket === version.current) await locate(ticket);
    });
    return cancel;
  }, [authenticated, run, loadFeatured, locate, cancel]);

  const refresh = () => {
    if (!authenticated || authError) return restore();
    return run(async ticket => {
      await loadFeatured(ticket);
      if (coordinates.current) {
        await locateWithSavedCoordinates(ticket);
      }
    });
  };
  const locateWithSavedCoordinates = async (ticket: number) => {
    const position = coordinates.current;
    if (!position) return;
    const next = await refreshLocation(position);
    if (ticket !== version.current) return;
    const local = next.feed.items.find(item => item.isLocal) ?? null;
    publishLocal(local, ticket);
    setDoePrices(next.doePrices);
    setLocationNote(local ? `Prices near ${next.area.resolved_area}.` :
      'No saved prices are available near your location yet.');
  };
  const useLocation = () => run(locate);
  const signOut = async () => {
    cancel();
    await logout();
  };
  return { featured, featuredError, localItem, doePrices, updates, historyError,
    busy: busy || authLoading, error: authError ?? error,
    locationNote, authenticated, refresh, useLocation, signOut };
}
