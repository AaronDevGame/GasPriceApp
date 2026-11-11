import { Link } from 'expo-router';
import { useState } from 'react';
import { ActivityIndicator, Linking, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, useWindowDimensions, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { LocalPriceBoard } from '@/components/local-price-board';
import { useTheme } from '@/hooks/use-theme';
import { formatPrice, type Prices, type FeaturedFuelPrice } from '@/fuel/api';
import { useFuelPrices } from '@/fuel/use-fuel-prices';
import { FuelNewsHeadline } from '@/components/fuel-news-headline';
import { DashboardAction } from '@/components/dashboard-action';
import { AppIcon } from '@/components/app-icon';
import { useLatestNews } from '@/news/use-latest-news';

function asOf(value: string) {
  return new Date(value).toLocaleDateString('en-PH', {
    year: 'numeric', month: 'short', day: 'numeric', timeZone: 'Asia/Manila',
  });
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
function FeaturedPriceCard({ item }: { item: FeaturedFuelPrice }) {
  return <ThemedView type="backgroundElement" style={styles.card}>
    <ThemedText style={styles.area}>{item.city}, {item.province}</ThemedText>
    <ThemedText type="small" themeColor="textSecondary">DOE data as of {asOf(item.dataAsOf)} · Report week {item.reportWeekStart} to {item.reportWeekEnd}</ThemedText>
    <PriceGrid prices={item.prices} />
    <Pressable accessibilityRole="link" onPress={() => void Linking.openURL(item.source.url)} style={styles.sourceLink}><ThemedText type="smallBold" style={styles.accent}>View DOE source ↗</ThemedText></Pressable>
  </ThemedView>;
}

export default function HomeScreen() {
  const fuel = useFuelPrices();
  const news = useLatestNews();
  const { width } = useWindowDimensions();
  const wide = width >= 1000;
  const theme = useTheme();
  const [refreshing, setRefreshing] = useState(false);
  const refreshHint = Platform.OS === 'web' ? 'Reload this page to reconnect or update prices.' : 'Pull down to reconnect or update prices.';
  const pullToRefresh = async () => {
    if (fuel.busy || refreshing) return;
    setRefreshing(true);
    try { await Promise.allSettled([fuel.refresh(), news.reload()]); } finally { setRefreshing(false); }
  };
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.safe}>
      <ScrollView contentContainerStyle={styles.scroll} keyboardShouldPersistTaps="handled"
        alwaysBounceVertical={Platform.OS === 'ios'}
        refreshControl={Platform.OS === 'web' ? undefined : <RefreshControl
          refreshing={refreshing} onRefresh={() => void pullToRefresh()} enabled={!fuel.busy}
          tintColor={theme.text} colors={[theme.text]} progressBackgroundColor={theme.backgroundElement}
        />}
      >
        <View style={styles.content}>
          <View style={[styles.mobileBrand, Platform.OS === 'web' && width >= 900 && { display: 'none' }]}>
            <AppIcon name="fuel" color="#76B4FF" />
            <ThemedText type="smallBold" style={styles.accent}>GASPRICE · PHILIPPINES</ThemedText>
          </View>
          {news.item && <FuelNewsHeadline item={news.item} />}
          {news.loading && !news.item && <View style={styles.loading}><ActivityIndicator /><ThemedText type="small" themeColor="textSecondary">Loading fuel news…</ThemedText></View>}
          {news.error && <View style={styles.loading}>
            <ThemedText type="small" themeColor="textSecondary">News could not be updated.</ThemedText>
            <DashboardAction title="Retry news" onPress={() => void news.reload()} disabled={news.loading} />
          </View>}
          {!news.item && !news.loading && !news.error && <ThemedText type="small" themeColor="textSecondary">No fuel news has been published yet.</ThemedText>}
          <ThemedView type="backgroundElement" style={styles.locationCard}>
            <View style={styles.locationText}>
              <ThemedText type="smallBold">{fuel.localItem ? `Your area · ${fuel.localItem.area.name}` : 'Where are you filling up?'}</ThemedText>
              <ThemedText type="small" themeColor="textSecondary">{fuel.locationNote}</ThemedText>
            </View>
            <View style={styles.locationActions}>
              <DashboardAction title="Use my location" primary disabled={fuel.busy || !fuel.authenticated} onPress={() => void fuel.useLocation()} />
              <Link href="/fuel-prices" asChild><Pressable accessibilityRole="link" style={styles.cityButton}><ThemedText type="smallBold" style={styles.accent}>Choose a city →</ThemedText></Pressable></Link>
            </View>
          </ThemedView>
          {!fuel.authenticated && !fuel.busy && !fuel.error && <ThemedText>You are signed out. {refreshHint}</ThemedText>}
          {fuel.error && <ThemedText accessibilityRole="alert">{fuel.error} {refreshHint}</ThemedText>}
          {fuel.busy && !refreshing && <View style={styles.loading}><ActivityIndicator /><ThemedText type="small">{fuel.featured ? 'Updating prices…' : 'Loading featured prices…'}</ThemedText></View>}
          {fuel.localItem && <LocalPriceBoard item={fuel.localItem} doePrices={fuel.doePrices} />}
          <View style={styles.history}>
            <ThemedText style={styles.area}>Fuel prices across the Philippines</ThemedText>
            <ThemedText type="small" themeColor="textSecondary">Featured DOE city prices are examples, not prices near you.</ThemedText>
            {fuel.featuredError && <ThemedText accessibilityRole="alert">Featured prices could not be updated. Pull down or reload to retry.</ThemedText>}
            {fuel.featured?.groups.map(group => <View key={group.name} style={styles.history}>
              <ThemedText style={styles.area}>{group.name}</ThemedText>
              {group.items.length ? <View style={[styles.featuredGrid, wide && styles.featuredGridWide]}>{group.items.map(item =>
                <View key={`${item.city}-${item.province}`} style={wide ? styles.featuredColumn : undefined}><FeaturedPriceCard item={item} /></View>)}</View> :
                <ThemedText type="small" themeColor="textSecondary">No recent DOE prices for the featured cities.</ThemedText>}
            </View>)}
          </View>
          {fuel.localItem && <View style={styles.history}>
            <ThemedText style={styles.area}>Recent updates</ThemedText>
            {fuel.updates.length ? <>
              <ThemedText type="small" themeColor="textSecondary">{fuel.localItem.area.name} · Newest first</ThemedText>
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
  page: { flex: 1 }, safe: { flex: 1 }, scroll: { padding: 20, alignItems: 'center' },
  content: { width: '100%', maxWidth: 1160, gap: 20, paddingVertical: 8, paddingBottom: 24 },
  mobileBrand: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingBottom: 4 },
  accent: { color: '#76B4FF' },
  loading: { flexDirection: 'row', flexWrap: 'wrap', gap: 12, alignItems: 'center' },
  locationCard: { borderRadius: 14, padding: 20, flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 16 },
  locationText: { flexGrow: 1, flexShrink: 1, flexBasis: 250, gap: 4 },
  locationActions: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 12 },
  cityButton: { minHeight: 44, paddingVertical: 12, paddingHorizontal: 4 },
  card: { padding: 20, borderRadius: 14, gap: 12 }, area: { fontSize: 19, lineHeight: 26, fontWeight: '600' },
  prices: { flexDirection: 'row', gap: 12, marginTop: 8 }, pricesCompact: { gap: 8 }, price: { flex: 1, minWidth: 0, gap: 4 },
  priceCaptionCompact: { fontSize: 12, lineHeight: 18 }, priceLabelNarrow: { minHeight: 36 },
  amount: { fontSize: 24, lineHeight: 32, fontWeight: '700', flexGrow: 1, fontVariant: ['tabular-nums'] },
  history: { gap: 16, marginTop: 8 }, featuredGrid: { gap: 12 }, featuredGridWide: { flexDirection: 'row', flexWrap: 'wrap' },
  featuredColumn: { flexBasis: '48%', flexGrow: 1, minWidth: 0 }, sourceLink: { minHeight: 44, justifyContent: 'center', alignSelf: 'flex-start' },
});
