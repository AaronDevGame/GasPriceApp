import { AppIcon } from '@/components/app-icon';
import { MobileHeader } from '@/components/mobile-header';
import { useIsFocused } from 'expo-router';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ActivityIndicator, Pressable, StyleSheet, TextInput, View, type ScrollView } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { ApiError, errorMessage } from '@/auth/http';
import { ThemedText } from '@/components/themed-text';
import { RefreshableScrollView } from '@/components/refreshable-scroll-view';
import { AppFooter } from '@/components/app-footer';
import { ThemedView } from '@/components/themed-view';
import { doeCoverageLabel, doeLocationLabel, getDoePriceBrowse, type DoeFuelPrice, type DoePriceBrowseFeed, type DoePriceLocation } from '@/fuel/api';
import { useTheme } from '@/hooks/use-theme';
import { useForegroundRefresh } from '@/hooks/use-foreground-refresh';
import { useFuelPrices } from '@/fuel/use-fuel-prices';
import { useHeaderComparison } from '@/fuel/header-prices';
import { LocalPriceBoard } from '@/components/local-price-board';
import { DoePriceSummary, MapPriceSummary, RecentPriceUpdates } from '@/components/fuel-price-summary';
import { DashboardAction } from '@/components/dashboard-action';
import { FuelNewsHeadline } from '@/components/fuel-news-headline';
import { useLatestNews } from '@/news/use-latest-news';
import { localLocation, mapAreaForLocation, sameLocation } from '@/fuel/selection';
import { mapCache } from '@/map/cache';
import FuelMap, { type FuelMapHandle } from '@/map/fuel-map';

const grades = [
  { value: 'DIESEL', label: 'Diesel' },
  { value: 'RON 91', label: 'Gasoline 91' },
  { value: 'RON 95', label: 'Gasoline 95' },
] as const;
const scrollPadding = 20;
const defaultLocation = { city: 'Manila', province: 'Metro Manila' };
const browseCacheMs = 5 * 60 * 1000;
type BrowseRequest = { promise: Promise<DoePriceBrowseFeed>; expiresAt: number };
type CompanyReport = { report?: DoePriceBrowseFeed['selected']; error?: string };

function browseKey(location?: DoePriceLocation) {
  return location ? JSON.stringify([location.city, location.province]) : '';
}

type PriceRange = Pick<DoeFuelPrice, 'minPricePerLiter' | 'maxPricePerLiter'>;

function companyRows(prices: DoeFuelPrice[], sortByPrice: boolean) {
  const companies = new Map<string, { name: string; prices: Map<string, PriceRange>; lowest: number }>();
  for (const price of prices) {
    if (!grades.some(grade => grade.value === price.fuelGrade)) continue;
    const company = companies.get(price.oilCompany) ?? {
      name: price.oilCompany, prices: new Map<string, PriceRange>(), lowest: Infinity,
    };
    const previous = company.prices.get(price.fuelGrade) ?? price;
    company.prices.set(price.fuelGrade, {
      minPricePerLiter: Math.min(previous.minPricePerLiter, price.minPricePerLiter),
      maxPricePerLiter: Math.max(previous.maxPricePerLiter, price.maxPricePerLiter),
    });
    company.lowest = Math.min(company.lowest, price.minPricePerLiter);
    companies.set(company.name, company);
  }
  return [...companies.values()].sort((a, b) =>
    (sortByPrice ? a.lowest - b.lowest : 0) || a.name.localeCompare(b.name));
}

function priceLabel(range: PriceRange) {
  const low = range.minPricePerLiter.toFixed(2);
  return range.minPricePerLiter === range.maxPricePerLiter ? low : `${low}–\u200b${range.maxPricePerLiter.toFixed(2)}`;
}

export default function FuelPricesScreen() {
  const focused = useIsFocused();
  const fuel = useFuelPrices();
  const { comparison, setComparison } = useHeaderComparison();
  const news = useLatestNews({ refreshOnForeground: false });
  const map = useRef<FuelMapHandle>(null);
  const scroll = useRef<ScrollView>(null);
  const [contentY, setContentY] = useState(0);
  const [comparisonY, setComparisonY] = useState<number | null>(null);
  const [mapRefreshing, setMapRefreshing] = useState(false);
  const { session, loading: authLoading, error: authError, restore } = useAuth();
  const theme = useTheme();
  const identity = session?.state === 'authenticated' ? String(session.playerId ?? 'authenticated') : null;
  const authenticated = identity !== null;
  const browseSession = useMemo(() => ({ identity }), [identity]);
  const browseRequests = useRef(new Map<string, BrowseRequest>());
  const [selection, setSelection] = useState<{ location: DoePriceLocation; focus: DoePriceLocation | null;
    session: typeof browseSession; cityId?: string } | null>(null);
  const [directory, setDirectory] = useState<{ session: typeof browseSession; locations?: DoePriceLocation[]; error?: string }>();
  const [details, setDetails] = useState<{ session: typeof browseSession; entries: Record<string, CompanyReport> }>();
  const [companyOpen, setCompanyOpen] = useState(false);
  const [reportRetry, setReportRetry] = useState(0);
  const [search, setSearch] = useState('');
  const [showCities, setShowCities] = useState(false);
  const [sortByPrice, setSortByPrice] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [, updateMapPrices] = useState(0);
  const manual = selection?.session === browseSession ? selection : null;
  const localArea = fuel.localItem?.area ?? fuel.resolvedArea;
  const detectedLocation = useMemo(() => localLocation(localArea, fuel.doePrices), [localArea, fuel.doePrices]);
  const deviceLocation = useMemo(() => localLocation(localArea, null), [localArea]);
  const mapLocation = manual ? manual.focus : deviceLocation;
  const visibleSelection = authenticated ? manual?.location ?? detectedLocation ??
    (!fuel.initialLocationPending && !fuel.userCoordinates ? defaultLocation : null) : null;
  const showingLocal = visibleSelection && detectedLocation && sameLocation(visibleSelection, detectedLocation);
  const showLocal = showingLocal ? fuel.localItem : null;
  const index = mapCache.index;
  const cityId = useMemo(() => {
    if (!index || !visibleSelection?.city) return undefined;
    if (visibleSelection === defaultLocation) return index.defaultCityId;
    return manual?.cityId ?? mapAreaForLocation(index, visibleSelection)?.members[0]?.id;
  }, [index, manual?.cityId, visibleSelection]);
  const cachedPrices = cityId ? mapCache.price(cityId) : undefined;
  const mapPrices = cachedPrices?.status !== 'unavailable' ? cachedPrices : undefined;
  useEffect(() => mapCache.subscribe(() => updateMapPrices(value => value + 1)), []);
  useEffect(() => {
    if (!cachedPrices) return;
    const timer = setTimeout(() => updateMapPrices(value => value + 1),
      Math.max(1, Date.parse(cachedPrices.expiresAtUtc) - Date.now()));
    return () => clearTimeout(timer);
  }, [cachedPrices]);

  useEffect(() => {
    const requests = new Map<string, BrowseRequest>();
    browseRequests.current = requests;
    return () => { requests.clear(); };
  }, [browseSession]);
  const browse = useCallback((location?: DoePriceLocation) => {
    const requests = browseRequests.current;
    const key = browseKey(location);
    const cached = requests.get(key);
    if (cached && cached.expiresAt > Date.now()) return cached.promise;
    const request: BrowseRequest = {
      promise: getDoePriceBrowse(location).then(result => {
        request.expiresAt = Date.now() + browseCacheMs;
        return result;
      }, failure => {
        if (location && failure instanceof ApiError && failure.status === 404) {
          request.expiresAt = Date.now() + browseCacheMs;
          return { locations: [], selected: null };
        }
        requests.delete(key);
        throw failure;
      }),
      expiresAt: Infinity,
    };
    requests.set(key, request);
    return request.promise;
  }, []);
  useEffect(() => {
    if (!authenticated || !showCities) return;
    let active = true;
    void browse().then(result => {
      if (active) setDirectory({ session: browseSession, locations: result.locations });
    }, failure => {
      if (active) setDirectory({ session: browseSession, error: errorMessage(failure) });
    });
    return () => { active = false; };
  }, [authenticated, browse, browseSession, showCities]);
  const reportKey = visibleSelection ? browseKey(visibleSelection) : '';
  const activeDetails = details?.session === browseSession ? details.entries[reportKey] : undefined;
  const saveDetails = useCallback((key: string, report: CompanyReport | undefined) => {
    setDetails(current => {
      const entries = current?.session === browseSession ? { ...current.entries } : {};
      if (report) entries[key] = report;
      else delete entries[key];
      return { session: browseSession, entries };
    });
  }, [browseSession]);
  useEffect(() => {
    if (!companyOpen || !visibleSelection) return;
    let active = true;
    const requests = browseRequests.current;
    const pending = browse(visibleSelection);
    // A response for a previously selected area can still populate that area's cache.
    void pending.then(result => {
      if (requests.get(reportKey)?.promise === pending) saveDetails(reportKey, { report: result.selected });
    }, failure => {
      if (active) saveDetails(reportKey, { error: errorMessage(failure) });
    });
    return () => { active = false; };
  }, [browse, companyOpen, reportKey, reportRetry, saveDetails, visibleSelection]);
  const choose = useCallback((location: DoePriceLocation, focus: DoePriceLocation | null = location, cityId?: string) => {
    setSelection({ location, focus, cityId, session: browseSession });
    setCompanyOpen(false);
    setShowCities(false);
    setSearch('');
  }, [browseSession]);
  const chooseOnMap = useCallback((location: DoePriceLocation, cityId?: string) => {
    choose(location, mapLocation, cityId);
  }, [choose, mapLocation]);
  useEffect(() => {
    if (!focused || !comparison || comparisonY === null || authLoading) return;
    const frame = requestAnimationFrame(() => {
      if (authenticated && comparison.location &&
          (!visibleSelection || !sameLocation(visibleSelection, comparison.location))) choose(comparison.location);
      if (authenticated) setCompanyOpen(true);
      scroll.current?.scrollTo({ y: contentY + comparisonY, animated: false });
      setComparison(null);
    });
    return () => cancelAnimationFrame(frame);
  }, [authenticated, authLoading, choose, comparison, comparisonY, contentY, focused, visibleSelection, setComparison]);

  const refreshingRef = useRef(false);
  const refresh = async () => {
    if (refreshingRef.current || fuel.busy) return;
    const requests = browseRequests.current;
    refreshingRef.current = true;
    setRefreshing(true);
    try {
      if (!authenticated) { await restore(); return; }
      await Promise.allSettled([fuel.refresh(), news.reload(), map.current?.refresh()]);
      if (companyOpen && visibleSelection && requests === browseRequests.current) {
        if (requests.get(reportKey)?.expiresAt !== Infinity) requests.delete(reportKey);
        setReportRetry(value => value + 1);
      }
    } finally { refreshingRef.current = false; setRefreshing(false); }
  };
  useForegroundRefresh(() => { void refresh(); });

  const areaDirectory = directory?.session === browseSession ? directory : undefined;
  const locations = areaDirectory?.locations?.filter(location =>
    `${location.city} ${location.province}`.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())) ?? [];
  const report = activeDetails?.report;
  const localReport = showingLocal && fuel.doePrices?.prices.length ? fuel.doePrices : null;
  const summaryReport = localReport ?? (report?.prices.length ? report : null);
  const rows = report ? companyRows(report.prices, sortByPrice) : [];
  const localPending = authenticated && !manual && !mapPrices && !summaryReport && !showLocal &&
    (fuel.initialLocationPending || fuel.busy);
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <RefreshableScrollView ref={scroll} contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled"
        refreshing={refreshing || mapRefreshing} onRefresh={refresh}
        refreshEnabled={!fuel.busy}>
        <MobileHeader style={styles.mobileHeader} />
        <FuelNewsHeadline items={news.items} />
        <View style={styles.content} onLayout={event => setContentY(event.nativeEvent.layout.y)}>
          {!authenticated && !authLoading && <ThemedText accessibilityRole="alert">{authError ?? 'You are signed out. Reconnect to load fuel prices.'}</ThemedText>}
          {!authenticated && !authLoading && <DashboardAction title="Reconnect" onPress={() => void restore()} />}
          <>
            <ThemedView type="backgroundElement" style={styles.card}>
              <View style={styles.locationHeading}>
                <ThemedText accessibilityRole="header" style={[styles.sectionTitle, styles.areaTitle]}>
                  {visibleSelection ? doeLocationLabel(visibleSelection) : fuel.initialLocationPending || detectedLocation ? 'Local fuel prices' : 'Choose an area'}
                </ThemedText>
                <View style={styles.actions}>
                  <Pressable accessibilityRole="button" accessibilityLabel="Choose city or province"
                    accessibilityState={{ expanded: showCities, disabled: !authenticated }} disabled={!authenticated}
                    onPress={() => {
                      if (!showCities && areaDirectory?.error) setDirectory(undefined);
                      setShowCities(!showCities);
                    }}
                    style={({ pressed }) => [styles.iconButton, { backgroundColor: theme.background, borderColor: showCities ? theme.text : theme.backgroundSelected, opacity: pressed || !authenticated ? 0.6 : 1 }]}>
                    <AppIcon name="search" color={theme.text} />
                  </Pressable>
                  <Pressable accessibilityRole="button" accessibilityLabel={fuel.userCoordinates ? 'Use my location' : 'Allow location access'}
                    accessibilityState={{ disabled: fuel.busy || !authenticated }} disabled={fuel.busy || !authenticated}
                    onPress={() => {
                      map.current?.clearSelection();
                      setSelection(null);
                      setCompanyOpen(false);
                      setShowCities(false);
                      void fuel.useLocation();
                    }}
                    style={({ pressed }) => [styles.iconButton, { backgroundColor: theme.background, borderColor: theme.backgroundSelected, opacity: pressed || fuel.busy || !authenticated ? 0.6 : 1 }]}>
                    <AppIcon name="location" color={fuel.userCoordinates ? '#76B4FF' : theme.textSecondary} />
                  </Pressable>
                </View>
              </View>
              {!manual && !localPending && (!fuel.userCoordinates || fuel.busy || fuel.error) && <ThemedText type="small" themeColor="textSecondary">{fuel.locationNote}</ThemedText>}
              {showCities && <View style={styles.cityList}>
                <TextInput autoFocus value={search} onChangeText={setSearch} placeholder="Search city or province"
                  placeholderTextColor={theme.textSecondary} accessibilityLabel="Search city or province"
                  style={[styles.search, { color: theme.text, borderColor: theme.backgroundSelected, backgroundColor: theme.background }]} />
                {locations.slice(0, 30).map(location => <Pressable key={`${location.city}|${location.province}`}
                  accessibilityRole="button" onPress={() => choose(location)}
                  style={styles.cityOption}>
                  <ThemedText>{location.city || location.province}</ThemedText>
                  <ThemedText type="small" themeColor="textSecondary">{location.city ? location.province : 'Province-level DOE range'}</ThemedText>
                </Pressable>)}
                {locations.length > 30 && <ThemedText type="small" themeColor="textSecondary">Showing the first 30 matches. Type to narrow the list.</ThemedText>}
                {locations.length === 0 && <ThemedText type="small" themeColor="textSecondary">No matching location.</ThemedText>}
              </View>}
              {authenticated && !manual && fuel.error && <ThemedText accessibilityRole="alert">{fuel.error}</ThemedText>}
              {localPending && !refreshing && <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">Updating local prices…</ThemedText></View>}
              {mapPrices ? <MapPriceSummary prices={mapPrices} /> : summaryReport ? <DoePriceSummary report={summaryReport} /> :
                showLocal ? <LocalPriceBoard item={showLocal} /> : (manual || cachedPrices) &&
                  <ThemedText themeColor="textSecondary">No map price ranges are available for this area.</ThemedText>}
            </ThemedView>
            <FuelMap ref={map} location={mapLocation} userCoordinates={fuel.userCoordinates}
              userLocation={deviceLocation}
              onReturnToLocation={() => { setSelection(null); setCompanyOpen(false); }}
              locations={areaDirectory?.locations} onLocationChange={chooseOnMap}
              initialLocationPending={fuel.initialLocationPending} foregroundRefreshing={refreshing || mapRefreshing} onForegroundRefreshChange={setMapRefreshing} />
          </>
          <View style={styles.comparison} onLayout={event => setComparisonY(event.nativeEvent.layout.y)}>
            {visibleSelection && <Pressable accessibilityRole="button" accessibilityState={{ expanded: companyOpen }}
              onPress={() => {
                if (!companyOpen && activeDetails?.error) saveDetails(reportKey, undefined);
                setCompanyOpen(!companyOpen);
              }}>
              <ThemedText type="smallBold" style={styles.accent}>{companyOpen ? 'Hide DOE company prices' : 'View DOE company prices'}</ThemedText>
            </Pressable>}
            {companyOpen && visibleSelection && !activeDetails && <View style={styles.loading} accessibilityLiveRegion="polite">
              <ActivityIndicator /><ThemedText>Loading DOE prices…</ThemedText>
            </View>}
            {companyOpen && activeDetails?.error && <View>
              <ThemedText accessibilityRole="alert">{activeDetails.error}</ThemedText>
              <DashboardAction title="Retry" onPress={() => { saveDetails(reportKey, undefined); setReportRetry(value => value + 1); }} />
            </View>}
            {companyOpen && activeDetails && !activeDetails.error && !report?.prices.length &&
              <ThemedText themeColor="textSecondary">No recent DOE company prices are available for this area.</ThemedText>}
            {companyOpen && !!report?.prices.length && <>
              <ThemedText style={styles.sectionTitle}>{doeCoverageLabel(report)}</ThemedText>
              <View style={styles.comparisonControls}>
                <Pressable accessibilityRole="button" onPress={() => setSortByPrice(value => !value)}>
                  <ThemedText type="smallBold" style={styles.accent}>Sort: {sortByPrice ? 'Lowest reported price' : 'Company name'} ↕</ThemedText>
                </Pressable>
                <ThemedText type="small" themeColor="textSecondary">DOE reported · ₱/L · {report.weekStart} to {report.weekEnd}</ThemedText>
              </View>
              {rows.length ? <ThemedView type="backgroundElement" style={styles.table}>
                <View style={styles.priceRow}>
                  <ThemedText type="smallBold" style={styles.company}>Company</ThemedText>
                  {grades.map(grade => <ThemedText key={grade.value} type="smallBold" style={styles.amount}>{grade.label}</ThemedText>)}
                </View>
                {rows.map(company => <View key={company.name} style={[styles.priceRow, { borderTopWidth: 1, borderColor: theme.backgroundSelected }]}>
                  <ThemedText type="smallBold" style={styles.company}>{company.name}</ThemedText>
                  {grades.map(grade => {
                    const range = company.prices.get(grade.value);
                    const label = range ? priceLabel(range) : '—';
                    return <ThemedText key={grade.value} type="small" style={styles.amount}
                      accessibilityLabel={`${company.name}, ${grade.label}, ${range ? label + ' pesos per liter' : 'not reported'}`}>
                      {label}
                    </ThemedText>;
                  })}
                </View>)}
              </ThemedView> : <ThemedText themeColor="textSecondary">No company prices for these fuels in the selected report.</ThemedText>}
            </>}
            {showLocal && <RecentPriceUpdates area={showLocal.area.name} updates={fuel.updates} error={fuel.historyError} />}
            {showLocal && <ThemedText type="small" themeColor="textSecondary">Location lookup uses Geoapify and OpenStreetMap contributors.</ThemedText>}
          </View>
        </View>
        <AppFooter />
      </RefreshableScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  locationHeading: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12 },
  page: { flex: 1 }, scroll: { flexGrow: 1, alignItems: 'center' },
  mobileHeader: { width: '100%', maxWidth: 1200, paddingHorizontal: scrollPadding, paddingTop: scrollPadding, paddingBottom: 10 },
  content: { width: '100%', maxWidth: 1200, gap: 16, padding: scrollPadding },
  accent: { color: '#76B4FF' },
  loading: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  card: { padding: 20, borderRadius: 14, gap: 12 },
  areaTitle: { flex: 1 }, actions: { flexDirection: 'row', gap: 8 },
  iconButton: { width: 44, height: 44, borderWidth: 1, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  cityList: { gap: 8 }, search: { borderWidth: 1, borderRadius: 10, padding: 12 },
  cityOption: { paddingVertical: 8, borderBottomWidth: 1, borderBottomColor: '#77777733' },
  sectionTitle: { fontSize: 22, lineHeight: 28, fontWeight: '700' },
  comparison: { gap: 16 },
  comparisonControls: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', alignItems: 'baseline', columnGap: 12, rowGap: 4 },
  table: { borderRadius: 14, paddingHorizontal: 8 },
  priceRow: { flexDirection: 'row', alignItems: 'center', gap: 6, paddingVertical: 12 },
  company: { flex: 1.15 }, amount: { flex: 1, textAlign: 'right', fontVariant: ['tabular-nums'] },
});
