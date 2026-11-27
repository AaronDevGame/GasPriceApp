import { useState } from 'react';
import { Pressable, StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { formatPrice, type Prices, type PricePoint, type DoeFuelPriceFeed } from '@/fuel/api';

function asOf(value: string) {
  return new Date(value).toLocaleDateString('en-PH', {
    year: 'numeric', month: 'short', day: 'numeric', timeZone: 'Asia/Manila',
  });
}
function PriceGrid({ prices, showUnit = true }: { prices: Prices; showUnit?: boolean }) {
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
        {showUnit && <ThemedText type="small" themeColor="textSecondary" style={compact && styles.priceCaptionCompact}>per liter</ThemedText>}
      </View>;
    })}
  </View>;
}

export function DoePriceSummary({ report }: { report: DoeFuelPriceFeed }) {
  const provinceWide = report.scope === 'province' || !report.city;
  const range = (grade: string) => {
    const rows = report.prices.filter(row => row.fuelGrade === grade);
    return { minPrice: rows.length ? Math.min(...rows.map(row => row.minPricePerLiter)) : null,
      maxPrice: rows.length ? Math.max(...rows.map(row => row.maxPricePerLiter)) : null,
      currency: 'PHP', unit: 'liter' };
  };
  return <>
    <PriceGrid prices={{ diesel: range('DIESEL'), gasoline91: range('RON 91'), gasoline95: range('RON 95') }} showUnit={false} />
    <ThemedText type="small" themeColor="textSecondary">
      DOE-reported {provinceWide ? 'province' : 'city'}-level company ranges per liter · {report.weekStart} to {report.weekEnd}
    </ThemedText>
  </>;
}
export function RecentPriceUpdates({ area, updates, error }: { area: string; updates: PricePoint[]; error: boolean }) {
  const [expanded, setExpanded] = useState(false);
  return <View style={styles.history}>
    <Pressable accessibilityRole="button" accessibilityState={{ expanded }} onPress={() => setExpanded(value => !value)} style={styles.toggle}>
      <ThemedText type="smallBold">Recent local estimate updates {expanded ? '↑' : '↓'}</ThemedText>
    </Pressable>
    {expanded && <>
      <ThemedText type="small" themeColor="textSecondary">{area} · Newest first</ThemedText>
      {updates.length ? updates.map(point => <ThemedView key={point.dataAsOf} type="backgroundElement" style={styles.card}>
        <ThemedText type="smallBold">{asOf(point.dataAsOf)}</ThemedText><PriceGrid prices={point.prices} />
      </ThemedView>) : <ThemedText type="small" themeColor="textSecondary">{error ? 'Recent updates are temporarily unavailable.' : 'No new updates yet.'}</ThemedText>}
    </>}
  </View>;
}
const styles = StyleSheet.create({
  prices: { flexDirection: 'row', gap: 12, marginTop: 8 }, pricesCompact: { gap: 8 }, price: { flex: 1, minWidth: 0, gap: 4 },
  priceCaptionCompact: { fontSize: 12, lineHeight: 18 }, priceLabelNarrow: { minHeight: 36 },
  amount: { fontSize: 24, lineHeight: 32, fontWeight: '700', flexGrow: 1, fontVariant: ['tabular-nums'] },
  card: { padding: 20, borderRadius: 14, gap: 12 }, history: { gap: 12 }, toggle: { minHeight: 44, justifyContent: 'center' },
});
