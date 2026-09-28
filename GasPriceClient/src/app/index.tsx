import { useState } from 'react';
import { ActivityIndicator, Button, ScrollView, StyleSheet, TextInput, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { useTheme } from '@/hooks/use-theme';
import { formatPrice, type Prices, type FeedItem } from '@/fuel/api';
import { useFuelPrices } from '@/fuel/use-fuel-prices';

function asOf(value: string) {
  return new Date(value).toLocaleDateString('en-PH', { year: 'numeric', month: 'short', day: 'numeric' });
}
function PriceGrid({ prices }: { prices: Prices }) {
  const [width, setWidth] = useState(0);
  const compact = width < 360;
  const amountSize = width < 280 ? 16 : compact ? 20 : 24;
  return <View style={[styles.prices, compact && styles.pricesCompact]} onLayout={event => setWidth(event.nativeEvent.layout.width)}>
    {(['diesel', 'gasoline91', 'gasoline95'] as const).map((key, index) => {
      const formatted = formatPrice(prices[key]);
      const stackedRange = width < 600 && formatted.includes('–');
      const fontSize = stackedRange && compact ? (width < 280 ? 14 : 16) : amountSize;
      return <View key={key} style={styles.price}>
        <ThemedText type="small" themeColor="textSecondary" style={[compact && styles.priceCaptionCompact, width < 280 && styles.priceLabelNarrow]}>{['Diesel', 'Gasoline 91', 'Gasoline 95'][index]}</ThemedText>
        <ThemedText style={[styles.amount, { fontSize, lineHeight: fontSize + 8 }]}>{stackedRange ? formatted.replace('–', '–\n') : formatted}</ThemedText>
        <ThemedText type="small" themeColor="textSecondary" style={compact && styles.priceCaptionCompact}>per liter</ThemedText>
      </View>;
    })}
  </View>;
}
function PriceCard({ item }: { item: FeedItem }) {
  const areaLabel = [...new Set([item.area.name, item.area.province, item.area.region].filter(Boolean))].join(', ');
  return <ThemedView type="backgroundElement" style={styles.card}>
    {item.isLocal && <ThemedText type="smallBold" style={styles.accent}>YOUR AREA</ThemedText>}
    <ThemedText style={styles.area}>{areaLabel}</ThemedText>
    <ThemedText type="small" themeColor="textSecondary">As of {asOf(item.dataAsOf)}{item.freshness !== 'current' ? ' · Older estimate' : ''}</ThemedText>
    <PriceGrid prices={item.prices} />
  </ThemedView>;
}

export default function HomeScreen() {
  const fuel = useFuelPrices();
  const theme = useTheme();
  const [city, setCity] = useState('');
  const [province, setProvince] = useState('');
  const [validation, setValidation] = useState<string | null>(null);
  const chooseArea = () => {
    if (!city.trim() && !province.trim()) { setValidation('Enter a city or province.'); return; }
    setValidation(null);
    void fuel.selectArea({ city: city.trim() || undefined, province: province.trim() || undefined });
  };
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.safe}>
      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled">
        <View style={styles.content}>
          <View style={styles.header}>
            <View style={styles.brand}>
              <ThemedText type="smallBold" style={styles.accent}>GASPRICE · PHILIPPINES</ThemedText>
              <ThemedText type="title" style={styles.title}>Fuel prices</ThemedText>
              <ThemedText themeColor="textSecondary">Diesel, 91 and 95 in ₱/liter.</ThemedText>
            </View>
            <View style={styles.actions}>
              <Button title="Refresh prices" disabled={fuel.busy} onPress={fuel.refresh} />
              <Button title="Logout" disabled={fuel.busy || !fuel.authenticated} onPress={() => void fuel.signOut()} />
            </View>
          </View>
          {!fuel.authenticated && !fuel.busy && !fuel.error && <ThemedText>You are signed out. Refresh prices to reconnect automatically.</ThemedText>}
          {fuel.error && <ThemedText accessibilityRole="alert">{fuel.error}</ThemedText>}
          {fuel.busy && <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">{fuel.feed ? 'Updating prices…' : 'Loading available prices…'}</ThemedText></View>}
          {fuel.feed?.items.map(item => <PriceCard key={JSON.stringify(item.area)} item={item} />)}
          {fuel.feed?.items.length === 0 && <ThemedView type="backgroundElement" style={styles.card}>
            <ThemedText style={styles.area}>No saved fuel prices yet</ThemedText>
            <ThemedText>Allow location access to look up prices near you.</ThemedText>
          </ThemedView>}
          <ThemedView type="backgroundElement" style={styles.card}>
            <ThemedText style={styles.area}>Find your local prices</ThemedText>
            <ThemedText type="small" themeColor="textSecondary">{fuel.locationNote}</ThemedText>
            <Button title="Use my location" disabled={fuel.busy || !fuel.authenticated} onPress={() => void fuel.useLocation()} />
            <ThemedText type="small">Or choose a Philippine city or province</ThemedText>
            <View style={styles.inputs}>
              {([{ label: 'City', value: city, set: setCity }, { label: 'Province', value: province, set: setProvince }]).map(field =>
                <TextInput key={field.label} accessibilityLabel={field.label} placeholder={field.label}
                  placeholderTextColor={theme.textSecondary} value={field.value} onChangeText={field.set}
                  maxLength={100} autoCapitalize="words" style={[styles.input, { color: theme.text, borderColor: theme.textSecondary }]}
                />)}
            </View>
            {validation && <ThemedText accessibilityRole="alert">{validation}</ThemedText>}
            <Button title="Show area prices" disabled={fuel.busy || !fuel.authenticated} onPress={chooseArea} />
          </ThemedView>
          {fuel.feed && <View style={styles.history}>
            <ThemedText style={styles.area}>Recent updates</ThemedText>
            {fuel.updates.length ? <>
              <ThemedText type="small" themeColor="textSecondary">{fuel.feed.items[0]?.area.name} · Newest first</ThemedText>
              {fuel.updates.map(point => <ThemedView key={point.dataAsOf} type="backgroundElement" style={styles.card}>
                <ThemedText type="smallBold">{asOf(point.dataAsOf)}</ThemedText><PriceGrid prices={point.prices} />
              </ThemedView>)}
            </> : <ThemedText type="small" themeColor="textSecondary">{fuel.historyError ? 'Recent updates are temporarily unavailable.' : 'No new updates yet.'}</ThemedText>}
          </View>}
          <ThemedText type="small" themeColor="textSecondary">Prices are area estimates and may vary by station. Location lookup uses Geoapify and OpenStreetMap contributors.</ThemedText>
        </View>
      </ScrollView>
    </SafeAreaView>
  </ThemedView>;
}
const styles = StyleSheet.create({
  page: { flex: 1 }, safe: { flex: 1 }, scroll: { padding: 24, alignItems: 'center' },
  content: { width: '100%', maxWidth: 900, gap: 20, paddingVertical: 16 },
  header: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 20, marginBottom: 12 },
  brand: { flexGrow: 1, gap: 8 }, title: { fontSize: 38, lineHeight: 44 },
  accent: { color: '#208AEF' }, actions: { gap: 8 }, loading: { flexDirection: 'row', gap: 12, alignItems: 'center' },
  card: { padding: 20, borderRadius: 20, gap: 12 }, area: { fontSize: 21, lineHeight: 28, fontWeight: '600' },
  prices: { flexDirection: 'row', gap: 12, marginTop: 8 }, pricesCompact: { gap: 8 },
  price: { flex: 1, minWidth: 0, gap: 4 },
  priceCaptionCompact: { fontSize: 12, lineHeight: 18 }, priceLabelNarrow: { minHeight: 36 },
  amount: { fontSize: 24, lineHeight: 32, fontWeight: '700', flexGrow: 1 },
  inputs: { flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  input: { flexGrow: 1, flexBasis: 180, borderWidth: 1, borderRadius: 10, padding: 12, fontSize: 16 },
  history: { gap: 12 },
});
