import { useCallback, useEffect, useRef, useState } from 'react';
import { useFocusEffect } from 'expo-router';
import { ActivityIndicator, Linking, Pressable, StyleSheet, View } from 'react-native';

import { useAuth } from '@/auth/auth-provider';
import { subscribeToForeground } from '@/auth/foreground';
import { ThemedText } from '@/components/themed-text';
import { MapController } from './controller';
import { mapCache } from './cache';
import MapRenderer from './map-renderer';
import type { MapSnapshot, Viewport } from './types';

const empty: MapSnapshot = { index: null, cities: [], features: [], prices: new Map(), viewport: null, loading: true, detail: false, error: null };
export default function FuelMap() {
  const { session, loading: authLoading } = useAuth();
  const authenticated = session?.state === 'authenticated';
  const [snapshot, setSnapshot] = useState(empty);
  const [selected, setSelected] = useState<string | null>(null);
  const controller = useRef<MapController | null>(null);
  const lastViewport = useRef<Viewport | null>(null);
  useEffect(() => {
    mapCache.setSession(authenticated ? String(session?.playerId ?? 'authenticated') : null);
  }, [authenticated, session?.playerId]);
  useFocusEffect(useCallback(() => {
    const next = new MapController();
    controller.current = next;
    const unsubscribe = next.subscribe(() => setSnapshot(next.getSnapshot()));
    next.setAuthenticated(authenticated);
    void next.start().then(() => { if (lastViewport.current) next.idle(lastViewport.current, true); });
    // Refresh cached expiries even while the map remains stationary, plus on foreground return.
    const interval = setInterval(next.refresh, 15000);
    const foreground = subscribeToForeground(next.refresh);
    return () => { clearInterval(interval); foreground(); unsubscribe(); next.dispose(); controller.current = null; };
  }, [authenticated]));
  const onIdle = useCallback((viewport: Viewport) => { lastViewport.current = viewport; controller.current?.idle(viewport); }, []);
  const onMove = useCallback(() => controller.current?.move(), []);
  const onSelect = useCallback((id: string) => { setSelected(id); controller.current?.select(id); }, []);
  const noPrices = snapshot.detail && !snapshot.loading && snapshot.cities.length > 0 &&
    ![...snapshot.prices.values()].some(price => price.gasoline || price.diesel);
  return <View style={styles.wrapper}>
    <View style={styles.canvas}>
      {snapshot.index ? <MapRenderer snapshot={snapshot} selected={selected} onMove={onMove} onIdle={onIdle} onSelect={onSelect} /> :
        <View style={styles.initial}><ActivityIndicator /><ThemedText>Loading Philippine map…</ThemedText></View>}
    </View>
    <View style={styles.status}>
      {snapshot.loading && <View style={styles.loading}><ActivityIndicator size="small" /><ThemedText type="small">Updating map…</ThemedText></View>}
      {!authenticated && !authLoading && <ThemedText type="small">Reconnect from Overview to load city prices.</ThemedText>}
      {snapshot.index && !snapshot.detail && <ThemedText type="small">Zoom in to see city price ranges.</ThemedText>}
      {noPrices && <ThemedText type="small">No current city prices are available in this view.</ThemedText>}
      {snapshot.error && <View style={styles.error}><ThemedText type="small" accessibilityRole="alert">{snapshot.error}</ThemedText>
        <Pressable accessibilityRole="button" onPress={() => controller.current?.retry()} style={styles.retry}><ThemedText type="smallBold">Retry</ThemedText></Pressable></View>}
      {/* Fuel descriptions belong outside the map; polygon overlays contain only the two ranges. */}
      <ThemedText type="small" themeColor="textSecondary">Top: gasoline RON 91 · Bottom: diesel · PHP per liter · — unavailable</ThemedText>
      <ThemedText type="small" themeColor="textSecondary">City report ranges; individual station prices vary.</ThemedText>
      <Pressable accessibilityRole="link" accessibilityLabel="Boundary source and license" onPress={() => void Linking.openURL('https://github.com/faeldon/philippines-json-maps')}>
        <ThemedText style={styles.attribution}>Boundaries: NAMRIA / PSA through OCHA COD-AB · CC BY 3.0 IGO · James Faeldon · MIT. Regrouped / ring orientation normalized by GasPriceApp.</ThemedText>
      </Pressable>
      <Pressable accessibilityRole="link" onPress={() => void Linking.openURL('https://creativecommons.org/licenses/by/3.0/igo/legalcode')}>
        <ThemedText style={styles.attribution}>Boundary license ↗</ThemedText>
      </Pressable>
    </View>
  </View>;
}
const styles = StyleSheet.create({
  wrapper: { gap: 10 }, canvas: { height: 560, minHeight: 380, borderRadius: 14, overflow: 'hidden', backgroundColor: '#202832' },
  initial: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 12 }, status: { gap: 8 },
  loading: { flexDirection: 'row', alignItems: 'center', gap: 8 }, error: { gap: 6 },
  retry: { alignSelf: 'flex-start', padding: 10, borderRadius: 8, backgroundColor: '#293B50' },
  attribution: { color: '#98A8BB', fontSize: 10, lineHeight: 15 },
});
