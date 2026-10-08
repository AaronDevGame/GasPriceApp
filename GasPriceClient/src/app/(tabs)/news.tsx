import { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Button, Platform, RefreshControl, ScrollView, StyleSheet, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';

import { ThemedText } from '@/components/themed-text';
import { ThemedView } from '@/components/themed-view';
import { subscribeToForeground } from '@/auth/foreground';
import { FuelNewsCard } from '@/components/fuel-news-card';
import { NewsAlerts } from '@/components/news-alerts';
import { getNewsPage, type NewsItem } from '@/news/api';

export default function NewsScreen() {
  const [items, setItems] = useState<NewsItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const generation = useRef(0);
  const busy = useRef(false);
  const load = useCallback(async (next?: string) => {
    if (next && busy.current) return;
    const current = ++generation.current;
    busy.current = true; setLoading(true);
    try {
      const result = await getNewsPage(next);
      if (generation.current !== current) return;
      setItems(previous => next ? [...previous, ...result.items.filter(item => !previous.some(p => p.id === item.id))] : result.items);
      setCursor(result.nextCursor); setError(false);
    } catch { if (generation.current === current) setError(true); }
    finally { if (generation.current === current) { busy.current = false; setLoading(false); } }
  }, []);
  useEffect(() => {
    const requests = generation;
    const current = ++requests.current;
    void getNewsPage().then(result => {
      if (requests.current === current) { setItems(result.items); setCursor(result.nextCursor); setError(false); }
    }, () => { if (requests.current === current) setError(true); })
      .finally(() => { if (requests.current === current) setLoading(false); });
    const unsubscribe = subscribeToForeground(() => void load());
    return () => { requests.current++; unsubscribe(); };
  }, [load]);
  return <ThemedView style={styles.page}>
    <SafeAreaView style={styles.page}>
      <ScrollView contentContainerStyle={styles.scroll} refreshControl={Platform.OS === 'web' ? undefined :
        <RefreshControl refreshing={loading && items.length > 0} onRefresh={() => void load()} />}>
        <View style={styles.content}>
          <ThemedText type="smallBold" style={styles.accent}>GASPRICE · PHILIPPINES</ThemedText>
          <ThemedText type="title" style={styles.title}>News</ThemedText>
          <ThemedText themeColor="textSecondary">Fuel announcements, forecasts, and the context behind price changes.</ThemedText>
          <NewsAlerts />
          {Platform.OS === 'web' && <Button title="Refresh news" onPress={() => void load()} disabled={loading} />}
          {error && <View style={{ gap: 8 }}><ThemedText accessibilityRole="alert">News is temporarily unavailable. Any loaded articles are retained.</ThemedText>
            <Button title="Retry" disabled={loading} onPress={() => void load()} /></View>}
          {loading && <ActivityIndicator accessibilityLabel="Loading fuel news" />}
          {!loading && !error && items.length === 0 && <ThemedText themeColor="textSecondary">No fuel news has been published yet.</ThemedText>}
          {items.map(item => <FuelNewsCard key={item.id} item={item} />)}
          {cursor && <Button title="Load older news" onPress={() => void load(cursor)} disabled={loading} />}
        </View>
      </ScrollView>
    </SafeAreaView>
  </ThemedView>;
}

const styles = StyleSheet.create({
  page: { flex: 1 },
  scroll: { padding: 24, alignItems: 'center' },
  content: { width: '100%', maxWidth: 900, gap: 20, paddingVertical: 16 },
  accent: { color: '#208AEF' },
  title: { fontSize: 38, lineHeight: 44 },
});
