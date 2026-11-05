import { Link } from 'expo-router';
import { Pressable, StyleSheet, View } from 'react-native';
import { ThemedText } from '@/components/themed-text';
import { AppIcon } from '@/components/app-icon';
import { newsDate, type NewsItem } from '@/news/api';
import { useNewsClock } from '@/news/use-news-clock';

export function FuelNewsHeadline({ item }: { item: NewsItem }) {
  const now = useNewsClock();
  const past = item.isExpired || Date.parse(item.article.expiresAtUtc) <= now;
  return <Link href={{ pathname: '/news-detail', params: { id: item.id } }} asChild>
    <Pressable accessibilityRole="link" accessibilityLabel={`Read fuel news: ${item.article.title}`} style={({ hovered, pressed }) => [styles.card, (hovered || pressed) && styles.hover]}>
      <View style={styles.metadata}>
        <View style={styles.label}><AppIcon name="news" color="#76B4FF" /><ThemedText type="smallBold" style={styles.blue}>FUEL NEWS</ThemedText></View>
        <ThemedText type="small" themeColor="textSecondary">{newsDate(item.publishedAtUtc)}</ThemedText>
      </View>
      <ThemedText accessibilityRole="header" style={styles.title}>{item.article.title}</ThemedText>
      {item.article.sources.length > 0 && <ThemedText type="small" themeColor="textSecondary">Source: {[...new Set(item.article.sources.map(source => source.name))].join(' · ')}</ThemedText>}
      <View style={styles.footer}>
        <ThemedText type="small" themeColor="textSecondary">{item.supersededById ? 'Superseded announcement' : past ? 'Past update' : item.article.status === 'forecast' ? 'Forecast · Subject to change' : item.article.status === 'confirmed' ? 'Confirmed announcement' : 'Latest update'}</ThemedText>
        <ThemedText type="smallBold" style={styles.blue}>Read full update →</ThemedText>
      </View>
    </Pressable>
  </Link>;
}
const styles = StyleSheet.create({
  card: { backgroundColor: '#182332', borderColor: '#2B415D', borderWidth: 1, borderRadius: 16, padding: 20, gap: 12 },
  hover: { backgroundColor: '#1D2C3E' }, metadata: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', justifyContent: 'space-between', gap: 10 },
  label: { flexDirection: 'row', alignItems: 'center', gap: 8 }, blue: { color: '#76B4FF' },
  title: { fontSize: 20, lineHeight: 28, fontWeight: '600' }, footer: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', gap: 10 },
});
