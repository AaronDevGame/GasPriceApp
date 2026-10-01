import { useState } from 'react';
import { StyleSheet, View } from 'react-native';

import { ThemedText } from '@/components/themed-text';
import { useColorScheme } from '@/hooks/use-color-scheme';
import { formatPrice, type FeedItem } from '@/fuel/api';

const fuels = [
  { key: 'diesel', grade: 'Diesel', label: 'Diesel', caption: 'FUEL', color: '#202326', tint: '#F1F2F3' },
  { key: 'gasoline91', grade: '91', label: 'Gasoline 91', caption: 'GASOLINE', color: '#087443', tint: '#EFF8F2' },
  { key: 'gasoline95', grade: '95', label: 'Gasoline 95', caption: 'GASOLINE', color: '#B92F38', tint: '#FFF2F1' },
] as const;

/** Only rendered for the feed's location-matched (Your area) item. */
export function LocalPriceBoard({ item }: { item: FeedItem }) {
  const dark = useColorScheme() === 'dark';
  const [width, setWidth] = useState(0);
  const columnWidth = (width - 16) / 3;
  const details = [...new Set([item.area.province, item.area.region].filter(value => value && value !== item.area.name))].join(' · ');
  const date = new Date(item.dataAsOf).toLocaleDateString('en-PH', { year: 'numeric', month: 'short', day: 'numeric' });

  return <View style={[styles.card, dark && styles.cardDark]}>
    <View style={styles.topLine} />
    <View style={styles.content} onLayout={event => setWidth(event.nativeEvent.layout.width)}>
      <View style={styles.heading}>
        <View style={[styles.badge, dark && styles.badgeDark]}>
          <ThemedText type="smallBold" style={[styles.badgeText, dark && styles.badgeTextDark]}>YOUR AREA</ThemedText>
        </View>
        <ThemedText type="small" themeColor="textSecondary">₱ / liter</ThemedText>
      </View>
      <View style={styles.location}>
        <ThemedText accessibilityRole="header" style={styles.area}>{item.area.name}</ThemedText>
        {details ? <ThemedText type="small" themeColor="textSecondary">{details}</ThemedText> : null}
      </View>
      <ThemedText type="small" themeColor="textSecondary">
        As of {date}{item.freshness !== 'current' ? ' · Older estimate' : ''}
      </ThemedText>
      <View style={styles.board}>
        {fuels.map(fuel => {
          const formatted = formatPrice(item.prices[fuel.key]);
          const range = formatted.includes('–');
          const missing = formatted === 'Not available';
          const fontSize = missing ? 13 : range ? Math.max(13, Math.min(22, columnWidth / 5)) : Math.max(16, Math.min(30, columnWidth / 3.8));
          return <View key={fuel.key} accessible accessibilityLabel={`${fuel.label}, ${formatted}${missing ? '' : ' per liter'}`}
            style={[styles.column, { backgroundColor: fuel.tint }]}>
            <View style={[styles.fuel, { backgroundColor: fuel.color }]}>
              <ThemedText style={[styles.grade, fuel.key === 'diesel' && styles.diesel]}>{fuel.grade}</ThemedText>
              <ThemedText style={styles.fuelCaption}>{fuel.caption}</ThemedText>
            </View>
            <View style={styles.price}>
              <ThemedText numberOfLines={range || missing ? 2 : 1} style={[styles.amount, { color: fuel.color, fontSize, lineHeight: fontSize + 5 }]}>
                {missing ? 'Not\navailable' : range ? formatted.replace('–', '–\n') : formatted}
              </ThemedText>
              {!missing && <ThemedText style={styles.unit}>per liter</ThemedText>}
            </View>
          </View>;
        })}
      </View>
      <ThemedText type="small" themeColor="textSecondary" style={styles.note}>Area estimates · Prices vary by station</ThemedText>
    </View>
  </View>;
}

const styles = StyleSheet.create({
  card: { backgroundColor: '#FFFDF8', borderWidth: 1, borderColor: '#E9E4D8', borderRadius: 24, overflow: 'hidden' },
  cardDark: { backgroundColor: '#191D20', borderColor: '#353B40' },
  topLine: { height: 5, backgroundColor: '#F4C95D' },
  content: { padding: 16, gap: 10 },
  heading: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
  badge: { backgroundColor: '#EAF3FF', borderRadius: 8, paddingHorizontal: 10, paddingVertical: 5 },
  badgeDark: { backgroundColor: '#22374D' },
  badgeText: { color: '#1764A3', fontSize: 11, lineHeight: 16, letterSpacing: 1.2 },
  badgeTextDark: { color: '#B8DCFF' },
  location: { gap: 4 },
  area: { fontSize: 24, lineHeight: 30, fontWeight: '700' },
  board: { flexDirection: 'row', gap: 8 },
  column: { flex: 1, minWidth: 0, borderRadius: 12, overflow: 'hidden' },
  fuel: { minHeight: 56, alignItems: 'center', justifyContent: 'center', paddingVertical: 6, paddingHorizontal: 4 },
  grade: { color: '#FFFFFF', fontSize: 24, lineHeight: 29, fontWeight: '800', fontVariant: ['tabular-nums'] },
  diesel: { fontSize: 17, lineHeight: 29 },
  fuelCaption: { color: '#FFFFFF', fontSize: 9, lineHeight: 14, fontWeight: '700', letterSpacing: 0.8 },
  price: { flex: 1, minWidth: 0, minHeight: 70, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 3, paddingVertical: 7 },
  amount: { fontWeight: '800', fontVariant: ['tabular-nums'], textAlign: 'center' },
  unit: { color: '#596269', fontSize: 11, lineHeight: 16 },
  note: { fontSize: 12, lineHeight: 18, textAlign: 'center' },
});
