import { AppIcon } from '@/components/app-icon';
import { MobileHeader } from '@/components/mobile-header';
import { useIsFocused } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Linking, Platform, Pressable, StyleSheet, TextInput, View, type ScrollView } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { errorMessage } from '@/auth/http';
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
import { DoePriceSummary, RecentPriceUpdates } from '@/components/fuel-price-summary';
import { DashboardAction } from '@/components/dashboard-action';
import { FuelNewsHeadline } from '@/components/fuel-news-headline';
import { useLatestNews } from '@/news/use-latest-news';
import { localLocation, sameLocation } from '@/fuel/selection';
import FuelMap, { type FuelMapHandle } from '@/map/fuel-map';

const grades = [
  { value: 'DIESEL', label: 'Diesel' },
  { value: 'RON 91', label: 'Gasoline 91' },
  { value: 'RON 95', label: 'Gasoline 95' },
] as const;
const scrollPadding = 20;

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
  const [historyVisible, setHistoryVisible] = useState(false);
  const followLocal = useRef(true);
  const { session, loading: authLoading, error: authError, restore } = useAuth();
  const theme = useTheme();
  const [feed, setFeed] = useState<DoePriceBrowseFeed | null>(null);
  const [selected, setSelected] = useState<DoePriceLocation | null>(null);
  const [selectedManually, setSelectedManually] = useState(false);
  const [search, setSearch] = useState('');
  const [showCities, setShowCities] = useState(false);
  const [sortByPrice, setSortByPrice] = useState(true);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);
  const authenticated = session?.state === 'authenticated';
  const showDefaultArea = !fuel.initialLocationPending && !fuel.userCoordinates;
  const visibleSelection = authenticated && (selectedManually || historyVisible || showDefaultArea) ? selected : null;
  const localArea = fuel.localItem?.area ?? fuel.resolvedArea;
  const detectedLocation = localLocation(localArea, fuel.doePrices);
  const localBrowsePending = authenticated && !!detectedLocation && !visibleSelection;
  const load = useCallback(async (location?: DoePriceLocation, refresh = false) => {
    const ticket = ++requestId.current;
    setLoading(true);
    setError(null);
    if (!refresh) setFeed(current => current ? { ...current, selected: null } : current);
    try {
      const next = await getDoePriceBrowse(location);
      if (ticket !== requestId.current) return;
      setFeed(next);
      if (!location) {
        setSelectedManually(false);
        setSelected(next.selected ? {
          city: next.selected.city ?? '', province: next.selected.province ?? '',
          scope: next.selected.scope ?? undefined,
        } : null);
      }
    } catch (failure) {
      if (ticket === requestId.current) setError(errorMessage(failure));
    } finally {
      if (ticket === requestId.current) setLoading(false);
    }
  }, []);
  const choose = useCallback((location: DoePriceLocation) => {
    followLocal.current = false;
    setSelected(location);
    setSelectedManually(true);
    setHistoryVisible(false);
    setShowCities(false);
    setSearch('');
    void load(location);
  }, [load]);
  useEffect(() => {
    if (!focused || !comparison || comparisonY === null || authLoading || (authenticated && loading)) return;
    const frame = requestAnimationFrame(() => {
      const area = authenticated ? comparison.location : null;
      if (area && (!visibleSelection || !sameLocation(visibleSelection, area))) {
        choose(area);
        return;
      }
      scroll.current?.scrollTo({ y: contentY + comparisonY, animated: false });
      setComparison(null);
    });
    return () => cancelAnimationFrame(frame);
  }, [authenticated, authLoading, choose, comparison, comparisonY, contentY, focused, loading, visibleSelection, setComparison]);
  useEffect(() => {
    if (!authenticated) return;
    let active = true;
    const ticket = ++requestId.current;
    void getDoePriceBrowse().then(next => {
      if (!active || ticket !== requestId.current) return;
      setFeed(next);
      setSelectedManually(false);
      setSelected(next.selected ? { city: next.selected.city ?? '', province: next.selected.province ?? '', scope: next.selected.scope ?? undefined } : null);
    }, failure => {
      if (active && ticket === requestId.current) setError(errorMessage(failure));
    }).finally(() => { if (active && ticket === requestId.current) setLoading(false); });
    return () => { active = false; };
  }, [authenticated]);
  useEffect(() => {
    if (!authenticated || !followLocal.current || fuel.busy) return;
    const location = localLocation(localArea, fuel.doePrices);
    if (!location || (historyVisible && selected && sameLocation(location, selected))) return;
    const ticket = ++requestId.current;
    let active = true;
    void (async () => {
      try {
        const next = await getDoePriceBrowse(location);
        if (!active || ticket !== requestId.current) return;
        setFeed(next);
        setError(null);
      } catch (failure) {
        if (!active || ticket !== requestId.current) return;
        setFeed(current => current ? { ...current, selected: null } : current);
        setError(errorMessage(failure));
      }
      setSelected(location);
      setSelectedManually(false);
      setHistoryVisible(true);
      setLoading(false);
    })();
    return () => { active = false; };
  }, [authenticated, localArea, fuel.doePrices, fuel.busy, historyVisible, selected]);
  useEffect(() => {
    const requests = requestId;
    return () => { ++requests.current; };
  }, []);

  const refreshingRef = useRef(false);
  const refresh = async () => {
    if (refreshingRef.current || (authenticated && loading) || fuel.busy || localBrowsePending) return;
    refreshingRef.current = true;
    setRefreshing(true);
    try {
      if (!authenticated) { await restore(); return; }
      await Promise.allSettled([load(selected ?? undefined, true), fuel.refresh(), news.reload(), map.current?.refresh()]);
    } finally { refreshingRef.current = false; setRefreshing(false); }
  };
  useForegroundRefresh(() => { void refresh(); });
  const showingLocal = visibleSelection && detectedLocation && sameLocation(visibleSelection, detectedLocation);
  const showLocal = showingLocal ? fuel.localItem : null;

  const locations = feed?.locations.filter(location =>
    `${location.city} ${location.province}`.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())) ?? [];
  const browsedReport = visibleSelection ? feed?.selected : null;
  const locationReport = showingLocal ? fuel.doePrices : null;
  const report = browsedReport?.prices.length ? browsedReport : locationReport?.prices.length ? locationReport : null;
  const rows = report ? companyRows(report.prices, sortByPrice) : [];
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <RefreshableScrollView ref={scroll} contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled"
        refreshing={refreshing || mapRefreshing} onRefresh={refresh}
        refreshEnabled={(!authenticated || !loading) && !fuel.busy && !localBrowsePending}>
        <MobileHeader style={styles.mobileHeader} />
        <FuelNewsHeadline items={news.items} />
        <View style={styles.content} onLayout={event => setContentY(event.nativeEvent.layout.y)}>
          {news.error && <View><ThemedText type="small" themeColor="textSecondary">News could not be updated.</ThemedText><DashboardAction title="Retry news" onPress={() => void news.reload()} disabled={news.loading} /></View>}
          {!authenticated && !authLoading && <ThemedText accessibilityRole="alert">{authError ?? 'You are signed out. Reconnect to load fuel prices.'}</ThemedText>}
          {!authenticated && !authLoading && <DashboardAction title="Reconnect" onPress={() => void restore()} />}
          {(authLoading || (authenticated && loading && !refreshing)) && <View style={styles.loading}><ActivityIndicator /><ThemedText>Loading DOE prices…</ThemedText></View>}
          {error && <ThemedText accessibilityRole="alert">{error} {Platform.OS === 'web' ? 'Pull down on a touch screen or reload to retry.' : 'Pull down to retry.'}</ThemedText>}
          {authenticated && !loading && feed?.locations.length === 0 &&
            <ThemedView type="backgroundElement" style={styles.card}>
              <ThemedText type="smallBold">No recent DOE prices are available yet.</ThemedText>
              <ThemedText themeColor="textSecondary">Prices will appear after a recent DOE report has been imported.</ThemedText>
            </ThemedView>}
          <>
            <ThemedView type="backgroundElement" style={styles.card}>
              <View style={styles.locationHeading}>
                <ThemedText accessibilityRole="header" style={[styles.sectionTitle, styles.areaTitle]}>
                  {visibleSelection ? doeLocationLabel(visibleSelection) : fuel.initialLocationPending || detectedLocation ? 'Local fuel prices' : 'Choose an area'}
                </ThemedText>
                <View style={styles.actions}>
                  <Pressable accessibilityRole="button" accessibilityLabel="Choose city or province"
                    accessibilityState={{ expanded: showCities, disabled: !authenticated }} disabled={!authenticated}
                    onPress={() => setShowCities(value => !value)}
                    style={({ pressed }) => [styles.iconButton, { backgroundColor: theme.background, borderColor: showCities ? theme.text : theme.backgroundSelected, opacity: pressed || !authenticated ? 0.6 : 1 }]}>
                    <AppIcon name="search" color={theme.text} />
                  </Pressable>
                  <Pressable accessibilityRole="button" accessibilityLabel={fuel.userCoordinates ? 'Use my location' : 'Allow location access'}
                    accessibilityState={{ disabled: fuel.busy || !authenticated }} disabled={fuel.busy || !authenticated}
                    onPress={() => { followLocal.current = true; setShowCities(false); void fuel.useLocation(); }}
                    style={({ pressed }) => [styles.iconButton, { backgroundColor: theme.background, borderColor: theme.backgroundSelected, opacity: pressed || fuel.busy || !authenticated ? 0.6 : 1 }]}>
                    <AppIcon name="location" color={fuel.userCoordinates ? '#76B4FF' : theme.textSecondary} />
                  </Pressable>
                </View>
              </View>
              {(!fuel.userCoordinates || fuel.busy || fuel.error) && <ThemedText type="small" themeColor="textSecondary">{fuel.locationNote}</ThemedText>}
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
              {authenticated && fuel.error && <ThemedText accessibilityRole="alert">{fuel.error}</ThemedText>}
              {authenticated && (fuel.busy || localBrowsePending) && !refreshing && <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">Updating local prices…</ThemedText></View>}
              {report ? <DoePriceSummary report={report} /> : showLocal && <LocalPriceBoard item={showLocal} />}
              {!loading && visibleSelection && !report && <ThemedText themeColor="textSecondary">No DOE company report is available for {doeLocationLabel(visibleSelection)}. Choose another area or explore the map.</ThemedText>}
            </ThemedView>
            <FuelMap ref={map} location={historyVisible ? localLocation(localArea, null) ?? selected : selectedManually ? selected : null} userCoordinates={fuel.userCoordinates}
              userLocation={localLocation(localArea, null)}
              onReturnToLocation={() => {
                const local = detectedLocation;
                if (!local) return;
                if (!selected || !sameLocation(local, selected)) choose(local);
                setHistoryVisible(true);
                followLocal.current = true;
              }} locations={feed?.locations ?? []} onLocationChange={choose}
              initialLocationPending={fuel.initialLocationPending} foregroundRefreshing={refreshing || mapRefreshing} onForegroundRefreshChange={setMapRefreshing} />
          </>
          <View style={styles.comparison} onLayout={event => setComparisonY(event.nativeEvent.layout.y)}>
            {report && <>
              <View style={styles.reportHeading}>
                <ThemedText style={styles.sectionTitle}>{doeCoverageLabel(report)}</ThemedText>
                <ThemedText type="small" themeColor="textSecondary">DOE reported · ₱/L · {report.weekStart} to {report.weekEnd}</ThemedText>
              </View>
              <Pressable accessibilityRole="button" onPress={() => setSortByPrice(value => !value)}>
                <ThemedText type="smallBold" style={styles.accent}>Sort: {sortByPrice ? 'Lowest reported price' : 'Company name'} ↕</ThemedText>
              </Pressable>
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
              {!!report.prices.length && <Pressable accessibilityRole="link" onPress={() => void Linking.openURL(report.prices[0].sourceUrl)}>
                <ThemedText type="smallBold" style={styles.accent}>View DOE report ↗</ThemedText>
              </Pressable>}
              <ThemedText type="small" themeColor="textSecondary">DOE reports {report.scope === 'province' ? 'province' : 'city'}-level prices by oil company. Individual station locations and availability are not included.</ThemedText>
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
  reportHeading: { gap: 4 }, sectionTitle: { fontSize: 22, lineHeight: 28, fontWeight: '700' },
  comparison: { gap: 16 },
  table: { borderRadius: 14, paddingHorizontal: 8 },
  priceRow: { flexDirection: 'row', alignItems: 'center', gap: 6, paddingVertical: 12 },
  company: { flex: 1.15 }, amount: { flex: 1, textAlign: 'right', fontVariant: ['tabular-nums'] },
});
