import { useState } from 'react';
import { Linking, Pressable, ScrollView, StyleSheet, View } from 'react-native';

import { ThemedText } from '@/components/themed-text';
import { cheapestDoePrices, formatPrice, type DoeFuelPriceFeed, type FeedItem } from '@/fuel/api';

const fuels = [
  { key: 'diesel', doeGrade: 'DIESEL', label: 'Diesel' },
  { key: 'gasoline91', doeGrade: 'RON 91', label: 'Gasoline 91' },
  { key: 'gasoline95', doeGrade: 'RON 95', label: 'Gasoline 95' },
] as const;

/** Only rendered for the feed's location-matched (Your area) item. */
export function LocalPriceBoard({ item, doePrices }: { item: FeedItem; doePrices?: DoeFuelPriceFeed | null }) {
  const [width, setWidth] = useState(0);
  const [selectedGrade, setSelectedGrade] = useState<string>('RON 91');
  const [expanded, setExpanded] = useState(false);
  const availableFuels = doePrices ? fuels.filter(fuel => cheapestDoePrices(doePrices, fuel.doeGrade).length) : [];
  const activeFuel = availableFuels.find(fuel => fuel.doeGrade === selectedGrade) ?? availableFuels[0];
  const cheapest = doePrices && activeFuel ? cheapestDoePrices(doePrices, activeFuel.doeGrade) : [];
  const displayedPrices = cheapest.slice(0, expanded ? 10 : 5);
  const compact = width < 640;
  const details = [...new Set([item.area.province, item.area.region].filter(value => value && value !== item.area.name))].join(' · ');
  const date = new Date(item.dataAsOf).toLocaleDateString('en-PH', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'Asia/Manila' });
  const priceRows = displayedPrices.map((price, index) => <View key={`${price.oilCompany}-${price.fuelGrade}`} style={styles.doeRow}>
    <ThemedText type="smallBold" themeColor="textSecondary" style={styles.rank}>{index + 1}</ThemedText>
    <ThemedText type="smallBold" style={styles.companyName}>{price.oilCompany}</ThemedText>
    <ThemedText type="smallBold" style={styles.doeAmount}>{formatPrice({
      minPrice: price.minPricePerLiter, maxPrice: price.maxPricePerLiter,
      currency: 'PHP', unit: 'liter',
    })}</ThemedText>
  </View>);

  return <View style={styles.sections}>
    <View style={styles.card}>
      <View style={styles.content} onLayout={event => setWidth(event.nativeEvent.layout.width)}>
        <View style={styles.heading}>
          <View style={styles.badge}>
            <ThemedText type="smallBold" style={styles.badgeText}>YOUR AREA</ThemedText>
          </View>
          <ThemedText type="small" themeColor="textSecondary">₱ / liter</ThemedText>
        </View>
        <View style={styles.location}>
          <ThemedText accessibilityRole="header" style={styles.area}>Fuel prices in {item.area.name}</ThemedText>
          {details ? <ThemedText type="small" themeColor="textSecondary">{details}</ThemedText> : null}
        </View>
        <ThemedText type="small" themeColor="textSecondary">
          As of {date}{item.freshness !== 'current' ? ' · Older estimate' : ''}
        </ThemedText>
        <View style={[styles.board, compact && styles.boardCompact]}>
          {fuels.map(fuel => {
            const formatted = formatPrice(item.prices[fuel.key]);
            const missing = formatted === 'Not available';
            const fontSize = missing ? 14 : compact ? (width < 360 ? 17 : 20) : 23;
            return <View key={fuel.key} accessible accessibilityLabel={`${fuel.label}, ${formatted}${missing ? '' : ' per liter'}`}
              style={[styles.column, compact && styles.columnCompact]}>
              <View style={styles.fuel}>
                <ThemedText style={styles.grade}>{fuel.label}</ThemedText>
              </View>
              <View style={[styles.price, compact && styles.priceCompact]}>
                <ThemedText style={[styles.amount, { fontSize, lineHeight: fontSize + 8 }]}>
                  {formatted}
                </ThemedText>
                {!missing && <ThemedText style={styles.unit}>per liter</ThemedText>}
              </View>
            </View>;
          })}
        </View>
        <ThemedText type="small" themeColor="textSecondary" style={styles.note}>Area estimates · Actual pump prices vary by station</ThemedText>
      </View>
    </View>
    {doePrices && cheapest.length > 0 && <View style={styles.doeSection}>
      <View style={styles.doeHeading}>
        <ThemedText type="smallBold">Compare company prices</ThemedText>
        <ThemedText type="small" themeColor="textSecondary">
          Top {displayedPrices.length} of {cheapest.length} available
        </ThemedText>
      </View>
      <ThemedText type="small" themeColor="textSecondary">
        {doePrices.city || item.area.name} · Week {doePrices.weekStart} to {doePrices.weekEnd}
      </ThemedText>
      <View style={styles.gradeTabs}>
        {availableFuels.map(fuel => <Pressable key={fuel.key} accessibilityRole="button"
          accessibilityState={{ selected: activeFuel.doeGrade === fuel.doeGrade }}
          onPress={() => { setSelectedGrade(fuel.doeGrade); setExpanded(false); }}
          style={[styles.gradeTab, activeFuel.doeGrade === fuel.doeGrade && styles.selectedTab]}>
          <ThemedText type="smallBold" style={activeFuel.doeGrade === fuel.doeGrade && styles.selectedTabText}>
            {fuel.label}
          </ThemedText>
        </Pressable>)}
      </View>
      {expanded ? <ScrollView style={styles.doeListExpanded} nestedScrollEnabled
        showsVerticalScrollIndicator={displayedPrices.length > 5}>{priceRows}</ScrollView> :
        <View>{priceRows}</View>}
      {cheapest.length > 5 && <Pressable accessibilityRole="button"
        accessibilityState={{ expanded }} onPress={() => setExpanded(value => !value)}
        style={styles.expandButton}>
        <ThemedText type="smallBold" style={styles.sourceLink}>
          {expanded ? 'Show top 5 ↑' : cheapest.length <= 10
            ? `Show all ${cheapest.length} ↓` : 'Show top 10 ↓'}
        </ThemedText>
      </Pressable>}
      <ThemedText type="small" themeColor="textSecondary" style={styles.doeNote}>
        Ranked by the low end of each reported range. DOE lists city-level company prices, not individual stations or nearby availability.
      </ThemedText>
      <Pressable accessibilityRole="link" onPress={() => void Linking.openURL(cheapest[0].sourceUrl)}>
        <ThemedText type="smallBold" style={styles.sourceLink}>View DOE report ↗</ThemedText>
      </Pressable>
    </View>}
  </View>;
}

const styles = StyleSheet.create({
  sections: { gap: 20 },
  card: { backgroundColor: '#1A2029', borderRadius: 16 },
  content: { padding: 20, gap: 12 },
  heading: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
  badge: { backgroundColor: '#21334B', borderRadius: 6, paddingHorizontal: 9, paddingVertical: 4 },
  badgeText: { color: '#76B4FF', fontSize: 10, lineHeight: 16, letterSpacing: 1.3 },
  location: { gap: 4 }, area: { fontSize: 24, lineHeight: 32, fontWeight: '600' },
  board: { flexDirection: 'row', gap: 12, marginTop: 4 }, boardCompact: { flexDirection: 'column', gap: 8 },
  column: { flex: 1, minWidth: 0, borderRadius: 12, backgroundColor: '#222B37', padding: 16, gap: 14 },
  columnCompact: { flex: 0, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 8, paddingVertical: 12 },
  fuel: { flexShrink: 1 }, grade: { fontSize: 14, lineHeight: 20, color: '#B5C1D2', fontWeight: '600' },
  price: { gap: 2, flexShrink: 1 }, priceCompact: { alignItems: 'flex-end' },
  amount: { fontWeight: '700', fontVariant: ['tabular-nums'], letterSpacing: -0.5 },
  unit: { color: '#A4AFBF', fontSize: 11, lineHeight: 16 }, note: { fontSize: 12, lineHeight: 18 },
  doeSection: { backgroundColor: '#1A2029', borderRadius: 16, padding: 20, gap: 12 },
  doeHeading: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', alignItems: 'center', gap: 8 },
  gradeTabs: { flexDirection: 'row', flexWrap: 'wrap', gap: 6, marginTop: 2 },
  gradeTab: { backgroundColor: '#222B37', borderWidth: 1, borderColor: 'transparent', borderRadius: 8, paddingHorizontal: 12, paddingVertical: 10, minHeight: 44 },
  selectedTab: { backgroundColor: '#21334B', borderColor: '#466C9B' }, selectedTabText: { color: '#76B4FF' },
  doeRow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 10, paddingVertical: 14, borderBottomWidth: 1, borderColor: '#2B3441' },
  doeListExpanded: { maxHeight: 320 }, expandButton: { alignSelf: 'flex-start', paddingVertical: 8 },
  rank: { width: 18 }, companyName: { flex: 1, minWidth: 80 },
  doeAmount: { textAlign: 'right', fontVariant: ['tabular-nums'] }, doeNote: { fontSize: 12, lineHeight: 18 },
  sourceLink: { color: '#76B4FF', paddingVertical: 8 },
});
