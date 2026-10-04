import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Linking, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, TextInput, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { useAuth } from '@/auth/auth-provider';
import { errorMessage } from '@/auth/http';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { getDoePriceBrowse, type DoeFuelPrice, type DoePriceBrowseFeed, type DoePriceLocation } from '@/fuel/api';
import { useTheme } from '@/hooks/use-theme';

const grades = [
  { value: 'DIESEL', label: 'Diesel' },
  { value: 'RON 91', label: 'Gasoline 91' },
  { value: 'RON 95', label: 'Gasoline 95' },
] as const;

function priceLabel(row: DoeFuelPrice) {
  const low = `₱${row.minPricePerLiter.toFixed(2)}`;
  return row.minPricePerLiter === row.maxPricePerLiter
    ? low : `${low}–₱${row.maxPricePerLiter.toFixed(2)}`;
}

export default function FuelPricesScreen() {
  const { session, loading: authLoading, error: authError } = useAuth();
  const theme = useTheme();
  const [feed, setFeed] = useState<DoePriceBrowseFeed | null>(null);
  const [selected, setSelected] = useState<DoePriceLocation | null>(null);
  const [grade, setGrade] = useState<string>('RON 91');
  const [search, setSearch] = useState('');
  const [showCities, setShowCities] = useState(false);
  const [sortByPrice, setSortByPrice] = useState(true);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const requestId = useRef(0);
  const authenticated = session?.state === 'authenticated';

  const load = useCallback(async (location?: DoePriceLocation, refresh = false) => {
    const ticket = ++requestId.current;
    if (refresh) setRefreshing(true);
    else setLoading(true);
    setError(null);
    try {
      const next = await getDoePriceBrowse(location);
      if (ticket !== requestId.current) return;
      setFeed(next);
      setSelected(location ?? (next.selected ? {
        city: next.selected.city!, province: next.selected.province!,
      } : null));
    } catch (failure) {
      if (ticket === requestId.current) setError(errorMessage(failure));
    } finally {
      if (ticket === requestId.current) { setLoading(false); setRefreshing(false); }
    }
  }, []);

  useEffect(() => {
    if (!authenticated) return;
    let active = true;
    const ticket = ++requestId.current;
    void getDoePriceBrowse().then(next => {
      if (!active || ticket !== requestId.current) return;
      setFeed(next);
      setSelected(next.selected ? {
        city: next.selected.city!, province: next.selected.province!,
      } : null);
    }, failure => {
      if (active && ticket === requestId.current) setError(errorMessage(failure));
    }).finally(() => {
      if (active && ticket === requestId.current) setLoading(false);
    });
    return () => { active = false; };
  }, [authenticated]);

  const locations = feed?.locations.filter(location =>
    `${location.city} ${location.province}`.toLocaleLowerCase().includes(search.trim().toLocaleLowerCase())) ?? [];
  const rows = feed?.selected?.prices.filter(row => row.fuelGrade === grade) ?? [];
  const sorted = [...rows].sort(sortByPrice
    ? (a, b) => a.minPricePerLiter - b.minPricePerLiter || a.maxPricePerLiter - b.maxPricePerLiter || a.oilCompany.localeCompare(b.oilCompany)
    : (a, b) => a.oilCompany.localeCompare(b.oilCompany));
  const report = feed?.selected;

  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled"
        refreshControl={Platform.OS === 'web' ? undefined : <RefreshControl refreshing={refreshing}
          onRefresh={() => void load(selected ?? undefined, true)} enabled={authenticated && !loading}
          tintColor={theme.text} colors={[theme.text]} progressBackgroundColor={theme.backgroundElement} />}>
        <View style={styles.content}>
          <View style={styles.header}>
            <ThemedText type="smallBold" style={styles.accent}>GASPRICE · PHILIPPINES</ThemedText>
            <ThemedText type="title" style={styles.title}>Fuel Prices</ThemedText>
            <ThemedText themeColor="textSecondary">Compare recent DOE company prices by city and fuel grade.</ThemedText>
          </View>
          {!authenticated && !authLoading && <ThemedText accessibilityRole="alert">{authError ?? 'You are signed out. Reopen the Overview tab to reconnect.'}</ThemedText>}
          {(authLoading || loading) && <View style={styles.loading}><ActivityIndicator /><ThemedText>Loading DOE prices…</ThemedText></View>}
          {error && <ThemedText accessibilityRole="alert">{error} {Platform.OS === 'web' ? 'Reload to retry.' : 'Pull down to retry.'}</ThemedText>}
          {authenticated && !loading && feed?.locations.length === 0 &&
            <ThemedView type="backgroundElement" style={styles.card}>
              <ThemedText type="smallBold">No recent DOE prices are available yet.</ThemedText>
              <ThemedText themeColor="textSecondary">Prices will appear after a recent DOE report has been imported.</ThemedText>
            </ThemedView>}
          {!!feed?.locations.length && <>
            <ThemedView type="backgroundElement" style={styles.card}>
              <ThemedText type="smallBold">City or municipality</ThemedText>
              <Pressable accessibilityRole="button" accessibilityLabel="Choose city" onPress={() => setShowCities(value => !value)}
                style={[styles.selector, { borderColor: theme.backgroundSelected }]}>
                <ThemedText>{selected ? `${selected.city}, ${selected.province}` : 'Choose a city'}</ThemedText>
                <ThemedText themeColor="textSecondary">{showCities ? '▲' : '▼'}</ThemedText>
              </Pressable>
              {showCities && <View style={styles.cityList}>
                <TextInput value={search} onChangeText={setSearch} placeholder="Search city or province"
                  placeholderTextColor={theme.textSecondary} accessibilityLabel="Search city or province"
                  style={[styles.search, { color: theme.text, borderColor: theme.backgroundSelected }]} />
                {locations.slice(0, 30).map(location => <Pressable key={`${location.city}|${location.province}`}
                  accessibilityRole="button" onPress={() => { setShowCities(false); setSearch(''); void load(location); }}
                  style={styles.cityOption}>
                  <ThemedText>{location.city}</ThemedText>
                  <ThemedText type="small" themeColor="textSecondary">{location.province}</ThemedText>
                </Pressable>)}
                {locations.length > 30 && <ThemedText type="small" themeColor="textSecondary">Showing the first 30 matches. Type to narrow the list.</ThemedText>}
                {locations.length === 0 && <ThemedText type="small" themeColor="textSecondary">No matching city.</ThemedText>}
              </View>}
            </ThemedView>
            {report && <>
              <View style={styles.reportHeading}>
                <ThemedText style={styles.sectionTitle}>{report.city}, {report.province}</ThemedText>
                <ThemedText type="small" themeColor="textSecondary">DOE report week · {report.weekStart} to {report.weekEnd}</ThemedText>
              </View>
              <View style={styles.controls}>
                {grades.map(item => <Pressable key={item.value} accessibilityRole="button"
                  accessibilityState={{ selected: grade === item.value }} onPress={() => setGrade(item.value)}
                  style={[styles.chip, { borderColor: grade === item.value ? '#208AEF' : theme.backgroundSelected,
                    backgroundColor: grade === item.value ? theme.backgroundSelected : theme.backgroundElement }]}>
                  <ThemedText type="smallBold" style={grade === item.value ? styles.accent : undefined}>{item.label}</ThemedText>
                </Pressable>)}
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
              <ThemedText type="small" themeColor="textSecondary">DOE reports city-level prices by oil company. Individual station locations and availability are not included.</ThemedText>
            </>}
          </>}
        </View>
      </ScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  page: { flex: 1 }, scroll: { padding: 24, alignItems: 'center' },
  content: { width: '100%', maxWidth: 900, gap: 16, paddingVertical: 16 },
  header: { gap: 8, marginBottom: 12 }, accent: { color: '#208AEF' },
  title: { fontSize: 38, lineHeight: 44 }, loading: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  card: { padding: 20, borderRadius: 20, gap: 12 },
  selector: { borderWidth: 1, borderRadius: 12, padding: 14, flexDirection: 'row', justifyContent: 'space-between' },
  cityList: { gap: 8 }, search: { borderWidth: 1, borderRadius: 10, padding: 12 },
  cityOption: { paddingVertical: 8, borderBottomWidth: 1, borderBottomColor: '#77777733' },
  reportHeading: { gap: 4 }, sectionTitle: { fontSize: 22, lineHeight: 28, fontWeight: '700' },
  controls: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { borderWidth: 1, borderRadius: 12, paddingHorizontal: 14, paddingVertical: 10 },
  priceRow: { borderRadius: 14, padding: 16, flexDirection: 'row', alignItems: 'center', gap: 12 },
  company: { flex: 1, gap: 4 }, amount: { textAlign: 'right', fontVariant: ['tabular-nums'] },
});
