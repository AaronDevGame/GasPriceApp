import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';

import { useAuth } from '@/auth/auth-provider';
import { isAppForeground, subscribeToAppVisibility } from '@/auth/foreground';
import { mapCache, MapRequestFailure } from '@/map/cache';
import type { City, CityPrices, GeographicIndex } from '@/map/types';
import { mapAreaForLocation } from './selection';
import type { Area, DoePriceLocation, FeedItem } from './api';

export type HeaderFuel = 'gasoline' | 'diesel';
type CityReport = { city: City; report: CityPrices };
type MapPrices = { local: CityReport | null; manila: CityReport | null; loading: boolean };
type PriceSnapshot = MapPrices & { area: Area | null; estimate: FeedItem | null };
type Comparison = { fuel: HeaderFuel; location?: DoePriceLocation };
const empty: PriceSnapshot = { local: null, manila: null, loading: false, area: null, estimate: null };

const LocationContext = createContext<(area: Area | null, estimate?: FeedItem | null) => void>(() => {});
const PricesContext = createContext(empty);
const ComparisonContext = createContext<{ comparison: Comparison | null; setComparison(value: Comparison | null): void }>({
  comparison: null, setComparison: () => {},
});
export const useSetHeaderLocation = () => useContext(LocationContext);
export const useHeaderPrices = () => useContext(PricesContext);
export const useHeaderComparison = () => useContext(ComparisonContext);

export function headerArea(area: Area | null) {
  return area?.city || area?.province || area?.region || null;
}

export function headerPrice(snapshot: PriceSnapshot, fuel: HeaderFuel) {
  const entry = snapshot.area ? snapshot.local : snapshot.manila;
  const range = entry?.report[fuel];
  if (!entry || !range) {
    const estimate = snapshot.estimate;
    const localRange = estimate?.prices[fuel === 'gasoline' ? 'gasoline91' : 'diesel'];
    if (!estimate || !localRange || (localRange.minPrice === null && localRange.maxPrice === null)) return null;
    const province = snapshot.area?.province || snapshot.area?.region;
    const location = province ? { city: snapshot.area?.city ?? '', province } : undefined;
    return { range: localRange, area: estimate.area.name, location, estimated: true,
      outdated: estimate.freshness !== 'current', baselineDate: null, localEstimate: true };
  }
  const { city, report } = entry;
  return { range, area: entry === snapshot.manila ? 'Manila' : city.name,
    location: { city: city.name, province: city.regionId === '1300000000' ? 'Metro Manila' : city.groupName },
    estimated: report.priceKind === 'estimated', outdated: report.isOutdated, localEstimate: false,
    baselineDate: report.monitoringEnd ?? report.weekEnd ?? (report.priceKind === 'estimated' ? null : report.asOf),
  };
}

function detectedCity(index: GeographicIndex, area: Area | null) {
  const province = area?.province || area?.region;
  return area?.city && province ? mapAreaForLocation(index, { city: area.city, province })?.members[0] : undefined;
}

export function HeaderPricesProvider({ children }: { children: ReactNode }) {
  const { session, loading } = useAuth();
  const identity = session?.state === 'authenticated' ? String(session.playerId ?? 'authenticated') : null;
  const [comparison, setComparison] = useState<Comparison | null>(null);
  const [location, setLocation] = useState<{ identity: string | null; area: Area | null; estimate: FeedItem | null }>();
  const detected = location?.identity === identity ? location : undefined;
  const area = detected?.area ?? null;
  const [snapshot, setSnapshot] = useState<{ identity: string; area: Area | null; prices: MapPrices }>();
  useEffect(() => {
    mapCache.setSession(identity);
    if (!identity) return;
    let mounted = true;
    let visible = isAppForeground();
    let running = false;
    let retryAt = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let city: City | undefined;
    let cities: City[] = [];

    const read = (city?: City) => {
      const report = city && mapCache.price(city.id);
      return city && report ? { city, report } : null;
    };
    const publish = () => {
      if (!mounted || !visible) return;
      const report = read(city);
      const prices = { manila: area ? null : report, local: area ? report : null, loading: running };
      setSnapshot(previous => previous?.identity === identity && previous.area === area &&
        previous.prices.manila?.report === prices.manila?.report && previous.prices.local?.report === prices.local?.report &&
        previous.prices.loading === prices.loading ? previous : { identity, area, prices });
    };
    const schedule = () => {
      clearTimeout(timer);
      if (!mounted || !visible || running) return;
      const due = retryAt > Date.now() ? retryAt : mapCache.nextPriceRefreshAt(cities);
      const deadline = Math.min(due, mapCache.nextPriceExpiry(cities));
      if (!Number.isFinite(deadline)) return;
      timer = setTimeout(() => {
        if (retryAt > Date.now()) { publish(); schedule(); }
        else void load();
      }, Math.max(1, deadline - Date.now()));
    };
    const load = async (refresh = false) => {
      if (!mounted || !visible || running) return;
      clearTimeout(timer);
      running = true;
      publish();
      try {
        const index = await mapCache.loadIndex();
        if (!mounted || !visible) return;
        city = area ? detectedCity(index, area) : index.cities.find(candidate => candidate.id === index.defaultCityId);
        cities = city ? [city] : [];
        publish();
        await mapCache.loadPrices(cities, refresh);
        retryAt = 0;
      } catch (error) {
        retryAt = error instanceof MapRequestFailure ? error.retryAt : Date.now() + 60000;
      } finally {
        running = false;
        publish();
        schedule();
      }
    };

    const unsubscribeCache = mapCache.subscribe(() => { publish(); schedule(); });
    const unsubscribeVisibility = subscribeToAppVisibility(next => {
      visible = next;
      if (visible) void load(true);
      else clearTimeout(timer);
    });
    void load();
    return () => {
      mounted = false;
      clearTimeout(timer);
      unsubscribeCache();
      unsubscribeVisibility();
    };
  }, [identity, area]);

  const setArea = useCallback((next: Area | null, estimate: FeedItem | null = null) => setLocation(previous => {
    const sameArea = previous?.identity === identity &&
      previous.area?.city === next?.city && previous.area?.province === next?.province && previous.area?.region === next?.region;
    if (sameArea && previous.estimate === estimate) return previous;
    return { identity, area: sameArea ? previous.area : next, estimate };
  }), [identity]);
  const prices = identity && snapshot?.identity === identity && snapshot.area === area
    ? snapshot.prices : { ...empty, loading: loading || Boolean(identity) };
  return <LocationContext.Provider value={setArea}>
    <PricesContext.Provider value={{ ...prices, area, estimate: detected?.estimate ?? null }}>
      <ComparisonContext.Provider value={{ comparison, setComparison }}>{children}</ComparisonContext.Provider>
    </PricesContext.Provider>
  </LocationContext.Provider>;
}
