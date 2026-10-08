import { Link } from 'expo-router';
import { StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { changeLabel, newsDate, type NewsItem } from '@/news/api';
import { useNewsClock } from '@/news/use-news-clock';

export function FuelNewsCard({ item, compact = false, detail = false }: { item: NewsItem; compact?: boolean; detail?: boolean }) {
  const a = item.article;
  const now = useNewsClock();
  const title = <ThemedText style={styles.title}>{a.title}</ThemedText>;
  return <ThemedView type="backgroundElement" style={styles.card}>
    <View style={styles.metadata}>
      <ThemedText type="smallBold" style={styles.badge}>{a.status === 'forecast' ? 'FORECAST' : a.status === 'confirmed' ? 'CONFIRMED' : 'FUEL NEWS'}</ThemedText>
      <ThemedText type="small" themeColor="textSecondary">{newsDate(item.publishedAtUtc)}</ThemedText>
    </View>
    {detail ? title : <Link href={{ pathname: '/news-detail', params: { id: item.id } }} accessibilityLabel={`Read ${a.title}`}>{title}</Link>}
    {item.supersededById ? <ThemedText type="small" themeColor="textSecondary">Superseded by a confirmed announcement.</ThemedText>
      : (item.isExpired || Date.parse(a.expiresAtUtc) <= now) && <ThemedText type="small" themeColor="textSecondary">Past update · retained for reference.</ThemedText>}
    {a.category === 'adjustment' && <>
      <ThemedText type="smallBold">{a.status === 'forecast' ? 'Expected adjustment' : 'Confirmed adjustment'} · PHP per liter</ThemedText>
      {(compact ? a.adjustments.slice(0, 3) : a.adjustments).map(change => <View key={`${change.fuel}-${change.oilCompany}`} style={styles.change}>
        <ThemedText type="smallBold">{change.fuel.charAt(0).toUpperCase() + change.fuel.slice(1)}{change.oilCompany ? ` · ${change.oilCompany}` : ''}</ThemedText>
        <ThemedText type="small">{changeLabel(change)}{a.status === 'forecast' && change.status === 'confirmed' ? ' · Confirmed' : ''}</ThemedText>
      </View>)}
      {compact && a.adjustments.length > 3 && <ThemedText type="small" themeColor="textSecondary">More company adjustments in the full update.</ThemedText>}
      {a.effectiveDatePhilippines && <ThemedText type="small" themeColor="textSecondary">Effective {a.effectiveAtUtc ? `${newsDate(a.effectiveAtUtc, true)} PHT` : `${newsDate(a.effectiveDatePhilippines)} · Time not announced`}</ThemedText>}
    </>}
    <ThemedText>{a.summary}</ThemedText>
    {!detail && <Link href={{ pathname: '/news-detail', params: { id: item.id } }} style={styles.link}>Read full update →</Link>}
  </ThemedView>;
}
const styles = StyleSheet.create({
  card: { padding: 20, borderRadius: 20, gap: 12 },
  metadata: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', gap: 8 },
  badge: { color: '#208AEF' }, title: { fontSize: 21, lineHeight: 28, fontWeight: '600' },
  change: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', gap: 8 },
  link: { color: '#208AEF', fontWeight: '600', paddingVertical: 6 },
});
