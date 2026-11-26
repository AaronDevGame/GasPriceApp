import { MobileHeader } from '@/components/mobile-header';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Button, Platform, StyleSheet, useWindowDimensions, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { ThemedText } from '@/components/themed-text';
import { RefreshableScrollView } from '@/components/refreshable-scroll-view';
import { ThemedView } from '@/components/themed-view';
import { subscribeToForeground } from '@/auth/foreground';
import { FuelNewsCard } from '@/components/fuel-news-card';
import { NewsAlerts } from '@/components/news-alerts';
import { getNewsPage, type NewsItem } from '@/news/api';

export default function NewsScreen() {
  const { width } = useWindowDimensions();
  const [items, setItems] = useState<NewsItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState(false);
  const [failedCursor, setFailedCursor] = useState<string | undefined>();
  const generation = useRef(0);
  const busy = useRef(false);
  const load = useCallback(async (next?: string, refresh = false) => {
    if (next && busy.current) return;
    const current = ++generation.current;
    busy.current = true; setLoading(true); setRefreshing(refresh);
    try {
      const result = await getNewsPage(next);
      if (generation.current !== current) return;
      setItems(previous => next ? [...previous, ...result.items.filter(item => !previous.some(p => p.id === item.id))] : result.items);
      setCursor(result.nextCursor); setError(false); setFailedCursor(undefined);
    } catch { if (generation.current === current) { setError(true); setFailedCursor(next); } }
    finally { if (generation.current === current) { busy.current = false; setLoading(false); setRefreshing(false); } }
  }, []);
  useEffect(() => {
    const requests = generation;
    const current = ++requests.current;
    void getNewsPage().then(result => {
      if (requests.current === current) { setItems(result.items); setCursor(result.nextCursor); setError(false); setFailedCursor(undefined); }
    }, () => { if (requests.current === current) setError(true); })
      .finally(() => { if (requests.current === current) setLoading(false); });
    const unsubscribe = subscribeToForeground(() => void load(undefined, true));
    return () => { requests.current++; unsubscribe(); };
  }, [load]);
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <RefreshableScrollView contentContainerStyle={styles.scroll} refreshing={refreshing}
        onRefresh={() => load(undefined, true)} refreshEnabled={!loading}>
        <View style={styles.content}>
          <MobileHeader />
          {Platform.OS === 'web' && width >= 900 && <ThemedText type="smallBold" style={styles.accent}>GasUp · PHILIPPINES</ThemedText>}
          <ThemedText type="title" style={styles.title}>News</ThemedText>
          <ThemedText themeColor="textSecondary">Fuel announcements, forecasts, and the context behind price changes.</ThemedText>
          <NewsAlerts />
          {error && <View style={{ gap: 8 }}><ThemedText accessibilityRole="alert">News is temporarily unavailable. Any loaded articles are retained.</ThemedText>
            <Button title="Retry" disabled={loading} onPress={() => void load(failedCursor)} /></View>}
          {loading && !refreshing && <ActivityIndicator accessibilityLabel="Loading fuel news" />}
          {!loading && !error && items.length === 0 && <ThemedText themeColor="textSecondary">No fuel news has been published yet.</ThemedText>}
          {items.map(item => <FuelNewsCard key={item.id} item={item} />)}
          {cursor && <Button title="Load older news" onPress={() => void load(cursor)} disabled={loading} />}
        </View>
      </RefreshableScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  page: { flex: 1 },
  scroll: { padding: 20, alignItems: 'center' },
  content: { width: '100%', maxWidth: 1160, gap: 20, paddingVertical: 12 },
  accent: { color: '#76B4FF' },
  title: { fontSize: 28, lineHeight: 36, fontWeight: '600' },
});
