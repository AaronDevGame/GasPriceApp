import { useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState, type Ref } from 'react';
import { useFocusEffect } from 'expo-router';
import { ActivityIndicator, Linking, Pressable, StyleSheet, View } from 'react-native';

import { useAuth } from '@/auth/auth-provider';
import { subscribeToForeground } from '@/auth/foreground';
import { AppIcon } from '@/components/app-icon';
import { ThemedText } from '@/components/themed-text';
import { MapController } from './controller';
import { mapCache } from './cache';
import MapRenderer from './map-renderer';
import { areaPrices, levelNames, mapLevel, mapLevelButtons, zoomForLevel, visibleAreas } from './hierarchy';
import { cameraCenter } from './geometry';
import { rangeText } from './labels';
import { mapAreaForLocation, locationForMapArea } from '@/fuel/selection';
import type { Coordinates, DoePriceLocation } from '@/fuel/api';
import type { MapSnapshot, Viewport, ZoomFocus } from './types';

const empty: MapSnapshot = { index: null, cities: [], features: [], prices: new Map(), viewport: null, loading: true, detail: false, error: null };
export type FuelMapHandle = { refresh(): Promise<void> };

export default function FuelMap({ ref, foregroundRefreshing = false, onForegroundRefreshChange, location, userCoordinates, onReturnToLocation, locations = [], onLocationChange, grade, active = true, managedRefresh = false }: {
  location?: DoePriceLocation | null;
  userCoordinates?: Coordinates | null;
  onReturnToLocation?(): void;
  locations?: DoePriceLocation[];
  onLocationChange?(location: DoePriceLocation): void;
  grade?: string;
  active?: boolean;
  managedRefresh?: boolean;
  ref?: Ref<FuelMapHandle>;
  foregroundRefreshing?: boolean;
  onForegroundRefreshChange?(refreshing: boolean): void;
}) {
  const { session, loading: authLoading } = useAuth();
  const authenticated = session?.state === 'authenticated';
  const [snapshot, setSnapshot] = useState(empty);
  const [mapSelection, setMapSelection] = useState<{ id: string; targetId: string | undefined } | null>(null);
  const [zoomFocus, setZoomFocus] = useState<ZoomFocus | null>(null);
  const [locationRequest, setLocationRequest] = useState(0);
  const controller = useRef<MapController | null>(null);
  const lastViewport = useRef<Viewport | null>(null);
  const refreshRequest = useRef<Promise<void> | null>(null);
  const refresh = useCallback(() => {
    const next = controller.current;
    if (!next) return Promise.resolve();
    if (refreshRequest.current) return refreshRequest.current;
    onForegroundRefreshChange?.(true);
    const request = next.refreshAndWait().finally(() => {
      if (controller.current === next) {
        refreshRequest.current = null;
        onForegroundRefreshChange?.(false);
      }
    });
    refreshRequest.current = request;
    return request;
  }, [onForegroundRefreshChange]);
  useImperativeHandle(ref, () => ({ refresh }), [refresh]);
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
    const foreground = managedRefresh ? () => {} : subscribeToForeground(() => { void refresh(); });
    return () => {
      clearInterval(interval); foreground(); unsubscribe(); next.dispose();
      controller.current = null; refreshRequest.current = null;
      onForegroundRefreshChange?.(false);
    };
  }, [authenticated, refresh, onForegroundRefreshChange, managedRefresh]));
  const onIdle = useCallback((viewport: Viewport) => { lastViewport.current = viewport; controller.current?.idle(viewport); }, []);
  const onMove = useCallback(() => controller.current?.move(), []);
  const locationCity = location?.city;
  const locationProvince = location?.province;
  const target = useMemo(() => snapshot.index && locationProvince !== undefined
    ? mapAreaForLocation(snapshot.index, { city: locationCity ?? '', province: locationProvince }) : null,
  [snapshot.index, locationCity, locationProvince]);
  const latitude = userCoordinates?.latitude;
  const longitude = userCoordinates?.longitude;
  const indexBounds = snapshot.index?.bbox;
  const canLocate = latitude !== undefined && longitude !== undefined && Number.isFinite(latitude) && Number.isFinite(longitude) &&
    !!indexBounds && longitude >= indexBounds[0] && longitude <= indexBounds[2] && latitude >= indexBounds[1] && latitude <= indexBounds[3];
  const locationFocus = useMemo(() => canLocate ? { latitude: latitude!, longitude: longitude!, request: locationRequest } : null,
    [canLocate, latitude, longitude, locationRequest]);
  const targetId = target?.id;
  const selected = mapSelection && mapSelection.targetId === targetId ? mapSelection.id : targetId ?? null;
  useEffect(() => {
    if (targetId) controller.current?.select(targetId);
  }, [targetId]);
  const onSelect = useCallback((id: string) => {
    setZoomFocus(null);
    setMapSelection({ id, targetId }); controller.current?.select(id);
    if (!snapshot.index || !snapshot.viewport) return;
    const area = visibleAreas(snapshot.index, snapshot.viewport).find(value => value.id === id);
    const next = area && locationForMapArea(area, locations);
    if (next) onLocationChange?.(next);
  }, [snapshot.index, snapshot.viewport, locations, onLocationChange, targetId]);
  const level = snapshot.index && snapshot.viewport ? mapLevel(snapshot.index, snapshot.viewport) : 'country';
  const areas = snapshot.index && snapshot.viewport ? visibleAreas(snapshot.index, snapshot.viewport) : [];
  const selection = areas.find(area => area.id === selected);
  const prices = selection ? areaPrices(selection, snapshot) : null;
  const noPrices = authenticated && !snapshot.loading && !snapshot.error && snapshot.prices.size > 0 &&
    [...snapshot.prices.values()].every(price => price.status === 'unavailable');
  return <View style={styles.wrapper}>
    {grade === 'RON 95' && <ThemedText type="small" themeColor="textSecondary">The map shows RON 91 and diesel. Choose Company list for RON 95 reports.</ThemedText>}
    <ThemedText type="small" themeColor="textSecondary">Select a reported city or province to update the company list. Broader areas show map ranges only.</ThemedText>
    <View style={styles.levelButtons} accessibilityLabel="Map geographic levels">
      {mapLevelButtons.map(item => <Pressable key={item.level} accessibilityRole="button"
        accessibilityLabel={`Show ${item.label.toLowerCase()}`} accessibilityHint={`Zoom the map to ${levelNames[item.level].toLowerCase()} level.`}
        accessibilityState={{ selected: !!snapshot.viewport && level === item.level, disabled: !snapshot.index || !snapshot.viewport }}
        disabled={!snapshot.index || !snapshot.viewport}
        onPress={() => {
          if (!snapshot.index || !snapshot.viewport) return;
          setZoomFocus({ zoom: zoomForLevel(snapshot.index, snapshot.viewport, item.level),
            ...(item.level === 'country' ? { center: cameraCenter(snapshot.index.bbox) } : {}) });
        }}
        style={[styles.levelButton, !!snapshot.viewport && level === item.level && styles.levelButtonSelected, !snapshot.viewport && styles.levelButtonDisabled]}>
        <ThemedText type="smallBold" style={!!snapshot.viewport && level === item.level ? styles.levelButtonTextSelected : undefined}>{item.label}</ThemedText>
      </Pressable>)}
    </View>
    <ThemedText type="small" themeColor="textSecondary">{levelNames[level]} · Zoom or choose a level</ThemedText>
    <View style={styles.canvas}>
      {snapshot.index ? <MapRenderer snapshot={snapshot} selected={selected} focusBounds={target?.bbox} locationFocus={locationFocus} zoomFocus={zoomFocus} active={active} onMove={onMove} onIdle={onIdle} onSelect={onSelect} /> :
        <View style={styles.initial}><ActivityIndicator /><ThemedText>Loading Philippine map…</ThemedText></View>}
      <Pressable accessibilityRole="button" accessibilityLabel="Return to my location"
        accessibilityHint={canLocate ? 'Center the map on your detected location.' : userCoordinates ? 'Your detected location is outside the Philippine map.' : 'Allow location access using Use my location above to enable this button.'}
        accessibilityState={{ disabled: !canLocate }} disabled={!canLocate}
        onPress={() => { setZoomFocus(null); setLocationRequest(value => value + 1); onReturnToLocation?.(); }}
        style={[styles.locationButton, canLocate ? styles.locationButtonEnabled : styles.locationButtonDisabled]}>
        <AppIcon name="location" color={canLocate ? '#76B4FF' : '#7B8798'} />
      </Pressable>
    </View>
    <View style={styles.status}>
      {selection && prices && <View style={styles.selection}>
        <ThemedText type="smallBold">{selection.name}</ThemedText>
        <ThemedText type="small">RON 91 {rangeText(prices.gasoline)} · Diesel {rangeText(prices.diesel)}</ThemedText>
        {level !== 'city' && <ThemedText type="small">{prices.reported} of {prices.total} cities / municipalities reporting</ThemedText>}
      </View>}
      {snapshot.loading && !foregroundRefreshing && <View style={styles.loading}><ActivityIndicator size="small" /><ThemedText type="small">Updating map…</ThemedText></View>}
      {!authenticated && !authLoading && <ThemedText type="small">Tap Reconnect above to load city prices.</ThemedText>}
      {noPrices && <ThemedText type="small">No current city prices are available in this view.</ThemedText>}
      {snapshot.error && <View style={styles.error}><ThemedText type="small" accessibilityRole="alert">{snapshot.error}</ThemedText>
        <Pressable accessibilityRole="button" onPress={() => controller.current?.retry()} style={styles.retry}><ThemedText type="smallBold">Retry</ThemedText></Pressable></View>}
      {/* Fuel descriptions belong outside the map; overlays show the location above the two ranges. */}
      <ThemedText type="small" themeColor="textSecondary">Top: gasoline RON 91 · Bottom: diesel · PHP per liter · — unavailable</ThemedText>
      <ThemedText type="small" themeColor="textSecondary">Area ranges combine the lowest–highest reported city prices, not averages. Coverage varies; individual station prices vary.</ThemedText>
      <Pressable accessibilityRole="link" accessibilityLabel="Boundary source and license" onPress={() => void Linking.openURL('https://github.com/faeldon/philippines-json-maps')}>
        <ThemedText style={styles.attribution}>Boundaries: NAMRIA / PSA through OCHA COD-AB · CC BY 3.0 IGO · James Faeldon · MIT. GasPriceApp regrouped city boundaries, normalized ring orientation, and merged and simplified area outlines.</ThemedText>
      </Pressable>
      <Pressable accessibilityRole="link" onPress={() => void Linking.openURL('https://creativecommons.org/licenses/by/3.0/igo/legalcode')}>
        <ThemedText style={styles.attribution}>Boundary license ↗</ThemedText>
      </Pressable>
    </View>
  </View>;
}
const styles = StyleSheet.create({
  levelButtons: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  levelButton: { minHeight: 44, paddingHorizontal: 14, paddingVertical: 10, borderWidth: 1, borderColor: '#495569', borderRadius: 12, justifyContent: 'center' },
  levelButtonSelected: { borderColor: '#76B4FF', backgroundColor: '#21334B' }, levelButtonTextSelected: { color: '#76B4FF' }, levelButtonDisabled: { opacity: 0.5 },
  locationButton: { position: 'absolute', left: 12, top: 12, width: 48, height: 48, borderRadius: 12, borderWidth: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: '#1A2029', zIndex: 20 },
  locationButtonEnabled: { borderColor: '#76B4FF' }, locationButtonDisabled: { borderColor: '#495569', opacity: 0.7 },
  selection: { padding: 12, borderRadius: 10, borderWidth: 1, borderColor: '#D38B00', gap: 4 },
  wrapper: { gap: 10 }, canvas: { height: 560, minHeight: 380, borderRadius: 14, overflow: 'hidden', backgroundColor: '#202832' },
  initial: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 12 }, status: { gap: 8 },
  loading: { flexDirection: 'row', alignItems: 'center', gap: 8 }, error: { gap: 6 },
  retry: { alignSelf: 'flex-start', padding: 10, borderRadius: 8, backgroundColor: '#293B50' },
  attribution: { color: '#98A8BB', fontSize: 10, lineHeight: 15 },
});
