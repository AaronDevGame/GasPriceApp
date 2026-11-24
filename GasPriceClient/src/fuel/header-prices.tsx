import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';

import { useAuth } from '@/auth/auth-provider';
import { isAppForeground, subscribeToAppVisibility } from '@/auth/foreground';
import { mapCache, MapRequestFailure } from '@/map/cache';
import type { City, CityPrices, GeographicIndex } from '@/map/types';
import { mapAreaForLocation } from './selection';
import type { Area, DoePriceLocation } from './api';

export type HeaderFuel = 'gasoline' | 'diesel';
type CityReport = { city: City; report: CityPrices };
type PriceSnapshot = { local: CityReport | null; manila: CityReport | null; loading: boolean };
type Comparison = { fuel: HeaderFuel; location?: DoePriceLocation };
const empty: PriceSnapshot = { local: null, manila: null, loading: false };

const LocationContext = createContext<(area: Area | null) => void>(() => {});
const PricesContext = createContext(empty);
const ComparisonContext = createContext<{ comparison: Comparison | null; setComparison(value: Comparison | null): void }>({
  comparison: null, setComparison: () => {},
});
export const useSetHeaderLocation = () => useContext(LocationContext);
export const useHeaderPrices = () => useContext(PricesContext);
export const useHeaderComparison = () => useContext(ComparisonContext);

export function headerPrice(snapshot: PriceSnapshot, fuel: HeaderFuel) {
  const entry = snapshot.local?.report[fuel] ? snapshot.local : snapshot.manila;
  const range = entry?.report[fuel];
  if (!entry || !range) return null;
  const { city, report } = entry;
  return { range, area: entry === snapshot.manila ? 'Manila' : city.name,
    location: { city: city.name, province: city.regionId === '1300000000' ? 'Metro Manila' : city.groupName },
    estimated: report.priceKind === 'estimated', outdated: report.isOutdated,
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
  const [location, setLocation] = useState<{ identity: string | null; area: Area | null }>();
  const area = location?.identity === identity ? location?.area ?? null : null;
  const [snapshot, setSnapshot] = useState<{ identity: string; area: Area | null; prices: PriceSnapshot }>();
  useEffect(() => {
    mapCache.setSession(identity);
    if (!identity) return;
    let mounted = true;
    let visible = isAppForeground();
    let running = false;
    let retryAt = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let cities: City[] = [];

    const read = (city?: City) => {
      const report = city && mapCache.price(city.id);
      return city && report ? { city, report } : null;
    };
    const publish = () => {
      if (!mounted || !visible) return;
      const [manila, local] = cities;
      const prices = { manila: read(manila), local: read(local), loading: running };
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
        const manila = index.cities.find(city => city.id === index.defaultCityId)!;
        const local = detectedCity(index, area);
        cities = local && local.id !== manila.id ? [manila, local] : [manila];
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

  const setArea = useCallback((next: Area | null) => setLocation(previous => previous?.identity === identity &&
    previous.area?.city === next?.city && previous.area?.province === next?.province && previous.area?.region === next?.region
    ? previous : { identity, area: next }), [identity]);
  const prices = identity && snapshot?.identity === identity && snapshot.area === area
    ? snapshot.prices : { ...empty, loading: loading || Boolean(identity) };
  return <LocationContext.Provider value={setArea}>
    <PricesContext.Provider value={prices}>
      <ComparisonContext.Provider value={{ comparison, setComparison }}>{children}</ComparisonContext.Provider>
    </PricesContext.Provider>
  </LocationContext.Provider>;
}
