import { Link, useLocalSearchParams } from 'expo-router';
import { useEffect, useState } from 'react';
import { ActivityIndicator, Button, Linking, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { ApiError } from '@/auth/http';
import { FuelNewsCard } from '@/components/fuel-news-card';
import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { getNewsItem, newsDate, newsIdPattern, type NewsItem } from '@/news/api';

// A static route plus query ID supports Expo's static web export.
export default function FuelNewsDetailScreen() {
  const { id } = useLocalSearchParams<{ id?: string | string[] }>();
  const validId = typeof id === 'string' && newsIdPattern.test(id) ? id : null;
  return <FuelNewsArticle key={validId ?? 'invalid'} validId={validId} />;
}

function FuelNewsArticle({ validId }: { validId: string | null }) {
  const [item, setItem] = useState<NewsItem | null>(null);
  const [loading, setLoading] = useState(!!validId);
  const [error, setError] = useState<string | null>(validId ? null : 'This news link is invalid.');
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    if (!validId) return;
    void getNewsItem(validId).then(result => { if (active) setItem(result); }, failure => {
      if (active) setError(failure instanceof ApiError && failure.status === 404 ? 'This article could not be found.' : 'This article is temporarily unavailable.');
    }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [validId, retry]);
  return <ThemedView style={styles.page}><SafeAreaView style={styles.page}>
    <ScrollView contentContainerStyle={styles.scroll}><View style={styles.content}>
      <Link href="/(tabs)/news" style={styles.link}>← All news</Link>
      {loading && <ActivityIndicator accessibilityLabel="Loading article" />}
      {error && <><ThemedText accessibilityRole="alert">{error}</ThemedText>{validId && <Button title="Retry" onPress={() => { setError(null); setLoading(true); setRetry(n => n + 1); }} />}</>}
      {item && <>
        <FuelNewsCard item={item} detail />
        {item.supersededById && <Link href={{ pathname: '/news-detail', params: { id: item.supersededById } }} style={styles.link}>Read the confirmed announcement →</Link>}
        {item.revision > 1 && <ThemedText type="small" themeColor="textSecondary">Updated {newsDate(item.updatedAtUtc, true)} PHT · Revision {item.revision}</ThemedText>}
        {item.article.body.split(/\n\s*\n/).map((paragraph, index) => <ThemedText key={index}>{paragraph}</ThemedText>)}
        {item.article.status === 'forecast' && <ThemedText type="smallBold">These are forecasts. Final amounts and company schedules may differ.</ThemedText>}
        <ThemedText type="smallBold">Sources</ThemedText>
        {item.article.sources.map(source => <View key={source.url} style={styles.source}>
          <Button title={source.name} onPress={() => {
            if (source.url.startsWith('https://')) void Linking.openURL(source.url).catch(() => setError('The source link could not be opened.'));
          }} />
          <ThemedText type="small" themeColor="textSecondary">Published {newsDate(source.publishedAtUtc)}</ThemedText>
        </View>)}
      </>}
    </View></ScrollView>
  </SafeAreaView></ThemedView>;
}
const styles = StyleSheet.create({
  page: { flex: 1 }, scroll: { padding: 24, alignItems: 'center' },
  content: { width: '100%', maxWidth: 900, gap: 20, paddingVertical: 16 },
  link: { color: '#208AEF', fontWeight: '600', paddingVertical: 8 }, source: { gap: 4, alignItems: 'flex-start' },
});
