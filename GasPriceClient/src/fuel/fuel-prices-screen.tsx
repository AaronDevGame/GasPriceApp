import { MobileHeader } from '@/components/mobile-header';
import { useIsFocused } from 'expo-router';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Linking, Platform, Pressable, StyleSheet, TextInput, View, type ScrollView } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { errorMessage } from '@/auth/http';
import { ThemedText } from '@/components/themed-text';
import { RefreshableScrollView } from '@/components/refreshable-scroll-view';
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

function priceLabel(row: DoeFuelPrice) {
  const low = `₱${row.minPricePerLiter.toFixed(2)}`;
  return row.minPricePerLiter === row.maxPricePerLiter
    ? low : `${low}–₱${row.maxPricePerLiter.toFixed(2)}`;
}

export default function FuelPricesScreen() {
  const focused = useIsFocused();
  const fuel = useFuelPrices();
  const { comparison, setComparison } = useHeaderComparison();
  const news = useLatestNews({ refreshOnForeground: false });
  const [mode, setMode] = useState<'map' | 'list'>('map');
  const map = useRef<FuelMapHandle>(null);
  const scroll = useRef<ScrollView>(null);
  const [contentY, setContentY] = useState(0);
  const [comparisonY, setComparisonY] = useState<number | null>(null);
  const [handledComparison, setHandledComparison] = useState<typeof comparison>(null);
  const [mapRefreshing, setMapRefreshing] = useState(false);
  const [historyVisible, setHistoryVisible] = useState(false);
  const followLocal = useRef(true);
  const { session, loading: authLoading, error: authError, restore } = useAuth();
  const theme = useTheme();
  const [feed, setFeed] = useState<DoePriceBrowseFeed | null>(null);
  const [selected, setSelected] = useState<DoePriceLocation | null>(null);
  const [selectedManually, setSelectedManually] = useState(false);
  const [grade, setGrade] = useState<string>('RON 91');
  const [search, setSearch] = useState('');
  const [showCities, setShowCities] = useState(false);
  const [sortByPrice, setSortByPrice] = useState(true);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);
  const authenticated = session?.state === 'authenticated';
  if (comparison !== handledComparison) {
    setHandledComparison(comparison);
    if (comparison) {
      setGrade(comparison.fuel === 'gasoline' ? 'RON 91' : 'DIESEL');
      setMode('list');
    }
  }
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
    if (!focused || !comparison || comparisonY === null || mode !== 'list' || authLoading || (authenticated && loading)) return;
    const frame = requestAnimationFrame(() => {
      const area = authenticated ? comparison.location : null;
      if (area && (!selected || !sameLocation(selected, area))) {
        choose(area);
        return;
      }
      scroll.current?.scrollTo({ y: contentY + comparisonY, animated: false });
      setComparison(null);
    });
    return () => cancelAnimationFrame(frame);
  }, [authenticated, authLoading, choose, comparison, comparisonY, contentY, focused, loading, mode, selected, setComparison]);
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
  const observedLocal = useRef(fuel.localItem);
  useEffect(() => {
    if (!authenticated || !followLocal.current || !fuel.localItem || fuel.busy || observedLocal.current === fuel.localItem) return;
    observedLocal.current = fuel.localItem;
    const location = localLocation(fuel.localItem, fuel.doePrices);
    if (!location || (historyVisible && selected && sameLocation(location, selected))) return;
    const ticket = ++requestId.current;
    let active = true;
    void getDoePriceBrowse(location).then(next => {
      if (!active || ticket !== requestId.current || !followLocal.current) return;
      setSelected(location);
      setFeed(next);
      setHistoryVisible(true);
      setError(null);
    }, failure => {
      if (!active || ticket !== requestId.current || !followLocal.current) return;
      setSelected(location);
      setFeed(current => current ? { ...current, selected: null } : current);
      setHistoryVisible(true);
      setError(errorMessage(failure));
    }).finally(() => { if (active && ticket === requestId.current) setLoading(false); });
    return () => { active = false; };
  }, [authenticated, fuel.localItem, fuel.doePrices, fuel.busy, historyVisible, selected]);
  useEffect(() => {
    const requests = requestId;
    return () => { ++requests.current; };
  }, []);

  const refreshingRef = useRef(false);
  const refresh = async () => {
    if (refreshingRef.current || (authenticated && loading) || fuel.busy) return;
    refreshingRef.current = true;
    setRefreshing(true);
    try {
      if (!authenticated) { await restore(); return; }
      await Promise.allSettled([load(selected ?? undefined, true), fuel.refresh(), news.reload(), map.current?.refresh()]);
    } finally { refreshingRef.current = false; setRefreshing(false); }
  };
  useForegroundRefresh(() => { void refresh(); });
  const showLocal = fuel.localItem && selected && sameLocation(selected, localLocation(fuel.localItem, fuel.doePrices) ?? { city: '', province: '' }) ? fuel.localItem : null;

  const locations = feed?.locations.filter(location =>
    `${location.city} ${location.province}`.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())) ?? [];
  const browsedReport = feed?.selected;
  const locationReport = showLocal ? fuel.doePrices : null;
  const report = browsedReport?.prices.length ? browsedReport : locationReport?.prices.length ? locationReport : null;
  const rows = report?.prices.filter(row => row.fuelGrade === grade) ?? [];
  const sorted = [...rows].sort(sortByPrice
    ? (a, b) => a.minPricePerLiter - b.minPricePerLiter || a.maxPricePerLiter - b.maxPricePerLiter || a.oilCompany.localeCompare(b.oilCompany)
    : (a, b) => a.oilCompany.localeCompare(b.oilCompany));
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <RefreshableScrollView ref={scroll} contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled"
        refreshing={refreshing || mapRefreshing} onRefresh={refresh}
        refreshEnabled={(!authenticated || !loading) && !fuel.busy}>
        <MobileHeader style={styles.mobileHeader} />
        <FuelNewsHeadline items={news.items} />
        <View style={styles.content} onLayout={event => setContentY(event.nativeEvent.layout.y)}>
          {news.error && <View><ThemedText type="small" themeColor="textSecondary">News could not be updated.</ThemedText><DashboardAction title="Retry news" onPress={() => void news.reload()} disabled={news.loading} /></View>}
          <View style={styles.header}>
            <ThemedText type="title" style={styles.title}>Fuel Prices</ThemedText>
            <ThemedText themeColor="textSecondary">Find prices in your area and compare reported company ranges.</ThemedText>
          </View>
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
                <ThemedText type="smallBold">Where are you filling up?</ThemedText>
                <DashboardAction title="Use my location" primary disabled={fuel.busy || !authenticated} onPress={() => { followLocal.current = true; void fuel.useLocation(); }} />
              </View>
              <ThemedText type="small" themeColor="textSecondary">{historyVisible || !selected || !fuel.userCoordinates ? fuel.locationNote : 'Browse reported prices or use your location for local estimates.'}</ThemedText>
              <Pressable accessibilityRole="button" accessibilityLabel="Choose city or province" accessibilityState={{ expanded: showCities, disabled: !authenticated }} disabled={!authenticated} onPress={() => setShowCities(value => !value)}
                style={[styles.selector, { borderColor: theme.backgroundSelected, backgroundColor: theme.background }]}>
                <ThemedText>{selected ? doeLocationLabel(selected) : 'Choose a city or province'}</ThemedText>
                <ThemedText themeColor="textSecondary">{showCities ? '▲' : '▼'}</ThemedText>
              </Pressable>
              {showCities && <View style={styles.cityList}>
                <TextInput value={search} onChangeText={setSearch} placeholder="Search city or province"
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
            </ThemedView>
            {authenticated && fuel.error && <ThemedText accessibilityRole="alert">{fuel.error}</ThemedText>}
            {authenticated && fuel.busy && !refreshing && <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">Updating local prices…</ThemedText></View>}
            {report ? <DoePriceSummary report={report} /> : showLocal && <LocalPriceBoard item={showLocal} />}
            {!loading && selected && !report && <ThemedText themeColor="textSecondary">No DOE company report is available for {doeLocationLabel(selected)}. Choose another area or explore the map.</ThemedText>}
            <View style={styles.controls} onLayout={event => setComparisonY(event.nativeEvent.layout.y)}>
              {grades.map(item => <Pressable key={item.value} accessibilityRole="button"
                accessibilityState={{ selected: grade === item.value }} onPress={() => setGrade(item.value)}
                style={[styles.chip, { borderColor: grade === item.value ? '#76B4FF' : theme.backgroundSelected }]}>
                <ThemedText type="smallBold">{item.label}</ThemedText>
              </Pressable>)}
            </View>
            <View style={styles.controls}>{(['map', 'list'] as const).map(value =>
              <Pressable key={value} accessibilityRole="button" accessibilityState={{ selected: mode === value }}
                onPress={() => setMode(value)} style={[styles.chip, { borderColor: mode === value ? '#76B4FF' : theme.backgroundSelected }]}>
                <ThemedText type="smallBold">{value === 'map' ? 'Map' : 'Company list'}</ThemedText>
              </Pressable>)}</View>
            <View style={mode === 'list' ? styles.hidden : undefined}>
              <FuelMap ref={map} location={historyVisible && fuel.localItem ? localLocation(fuel.localItem, null) ?? selected : selectedManually ? selected : null} userCoordinates={fuel.userCoordinates}
                userLocation={fuel.localItem ? localLocation(fuel.localItem, null) : null}
                onReturnToLocation={() => {
                  if (!fuel.localItem) return;
                  const local = localLocation(fuel.localItem, fuel.doePrices);
                  if (local && (!selected || !sameLocation(local, selected))) choose(local);
                  setHistoryVisible(true);
                  followLocal.current = true;
                }} locations={feed?.locations ?? []} onLocationChange={choose}
                grade={grade} active={mode === 'map'} initialLocationPending={fuel.busy && !fuel.localItem} foregroundRefreshing={refreshing || mapRefreshing} onForegroundRefreshChange={setMapRefreshing} />
            </View>
            {mode === 'list' && report && <>
              <View style={styles.reportHeading}>
                <ThemedText style={styles.sectionTitle}>{doeCoverageLabel(report)}</ThemedText>
                <ThemedText type="small" themeColor="textSecondary">DOE reported · {report.weekStart} to {report.weekEnd}</ThemedText>
              </View>
              <Pressable accessibilityRole="button" onPress={() => setSortByPrice(value => !value)}>
                <ThemedText type="smallBold" style={styles.accent}>Sort: {sortByPrice ? 'Lowest reported price' : 'Company name'} ↕</ThemedText>
              </Pressable>
              {sorted.length ? sorted.map((row, index) => <ThemedView key={`${row.oilCompany}|${row.fuelGrade}|${index}`}
                type="backgroundElement" style={styles.priceRow}>
                <View style={styles.company}>
                  <ThemedText type="smallBold">{row.oilCompany}</ThemedText>
                  <ThemedText type="small" themeColor="textSecondary">{row.fuelGrade}</ThemedText>
                </View>
                <ThemedText type="smallBold" style={styles.amount}>{priceLabel(row)} / L</ThemedText>
              </ThemedView>) : <ThemedText themeColor="textSecondary">No prices for this fuel grade in the selected report.</ThemedText>}
              {!!report.prices.length && <Pressable accessibilityRole="link" onPress={() => void Linking.openURL(report.prices[0].sourceUrl)}>
                <ThemedText type="smallBold" style={styles.accent}>View DOE report ↗</ThemedText>
              </Pressable>}
              <ThemedText type="small" themeColor="textSecondary">DOE reports {report.scope === 'province' ? 'province' : 'city'}-level prices by oil company. Individual station locations and availability are not included.</ThemedText>
            </>}
          </>
          {showLocal && <RecentPriceUpdates area={showLocal.area.name} updates={fuel.updates} error={fuel.historyError} />}
          {showLocal && <ThemedText type="small" themeColor="textSecondary">Location lookup uses Geoapify and OpenStreetMap contributors.</ThemedText>}
        </View>
      </RefreshableScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  hidden: { display: 'none' }, locationHeading: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 12 },
  page: { flex: 1 }, scroll: { alignItems: 'center' },
  mobileHeader: { width: '100%', maxWidth: 1200, paddingHorizontal: scrollPadding, paddingTop: scrollPadding },
  content: { width: '100%', maxWidth: 1200, gap: 16, padding: scrollPadding },
  header: { gap: 8, marginBottom: 12 }, accent: { color: '#76B4FF' },
  title: { fontSize: 28, lineHeight: 36, fontWeight: '600' }, loading: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  card: { padding: 20, borderRadius: 14, gap: 12 },
  selector: { borderWidth: 1, borderRadius: 12, padding: 14, flexDirection: 'row', justifyContent: 'space-between' },
  cityList: { gap: 8 }, search: { borderWidth: 1, borderRadius: 10, padding: 12 },
  cityOption: { paddingVertical: 8, borderBottomWidth: 1, borderBottomColor: '#77777733' },
  reportHeading: { gap: 4 }, sectionTitle: { fontSize: 22, lineHeight: 28, fontWeight: '700' },
  controls: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { borderWidth: 1, borderRadius: 12, paddingHorizontal: 14, paddingVertical: 10 },
  priceRow: { borderRadius: 14, padding: 16, flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 12 },
  company: { flex: 1, minWidth: 110, gap: 4 }, amount: { textAlign: 'right', fontVariant: ['tabular-nums'] },
});
