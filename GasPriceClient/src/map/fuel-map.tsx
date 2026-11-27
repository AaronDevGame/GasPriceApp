import { useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState, type Ref } from 'react';
import { useIsFocused } from 'expo-router';
import { ActivityIndicator, Linking, Pressable, StyleSheet, View } from 'react-native';

import { useAuth } from '@/auth/auth-provider';
import { isAppForeground, subscribeToAppVisibility } from '@/auth/foreground';
import { AppIcon } from '@/components/app-icon';
import { ThemedText } from '@/components/themed-text';
import { MapController } from './controller';
import { mapCache } from './cache';
import MapRenderer from './map-renderer';
import { defaultMapCamera, levelNames, mapLevel, mapLevelButtons, metroManilaBounds, zoomForLevel, visibleAreas } from './hierarchy';
import { cameraCenter } from './geometry';
import { mapAreaForLocation, locationForMapArea } from '@/fuel/selection';
import type { Coordinates, DoePriceLocation } from '@/fuel/api';
import type { LocationFocus, MapSnapshot, Viewport, ZoomFocus } from './types';

const empty: MapSnapshot = { index: null, cities: [], features: [], prices: new Map(), viewport: null, loading: true, detail: false, error: null };
const showMapCredits = false;
export type FuelMapHandle = { refresh(): Promise<void> };

export default function FuelMap({ ref, foregroundRefreshing = false, onForegroundRefreshChange, location, userLocation, userCoordinates, onReturnToLocation, locations = [], onLocationChange, active = true, initialLocationPending = false }: {
  location?: DoePriceLocation | null;
  userCoordinates?: Coordinates | null;
  userLocation?: DoePriceLocation | null;
  onReturnToLocation?(): void;
  locations?: DoePriceLocation[];
  onLocationChange?(location: DoePriceLocation, cityId?: string): void;
  active?: boolean;
  initialLocationPending?: boolean;
  ref?: Ref<FuelMapHandle>;
  foregroundRefreshing?: boolean;
  onForegroundRefreshChange?(refreshing: boolean): void;
}) {
  const { session, loading: authLoading } = useAuth();
  const authenticated = session?.state === 'authenticated';
  const focused = useIsFocused();
  const eligible = active && focused;
  const eligibility = useRef(eligible);
  const authState = useRef(authenticated);
  const locating = useRef(initialLocationPending);
  const updateActivity = useRef<((visible: boolean) => void) | null>(null);
  const [showCredits, setShowCredits] = useState(false);
  const [snapshot, setSnapshot] = useState(empty);
  const [mapSelection, setMapSelection] = useState<{ id: string | null; targetId: string | undefined } | null>(null);
  const [zoomCommand, setZoomCommand] = useState<{ focus: ZoomFocus; location: LocationFocus | null } | null>(null);
  const [locationRequest, setLocationRequest] = useState(0);
  const controller = useRef<MapController | null>(null);
  const lastViewport = useRef<Viewport | null>(null);
  const refreshRequest = useRef<Promise<void> | null>(null);
  const refresh = useCallback(() => {
    const next = controller.current;
    if (!next?.isActive()) return Promise.resolve();
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
    controller.current?.resetPriceRequests();
  }, [authenticated, session?.playerId]);
  useEffect(() => {
    const next = new MapController();
    controller.current = next;
    const unsubscribe = next.subscribe(() => setSnapshot(next.getSnapshot()));
    next.setActive(eligibility.current && isAppForeground());
    next.setAuthenticated(authState.current);
    void next.start().then(() => { if (lastViewport.current) next.idle(lastViewport.current, true); });
    const activity = (visible: boolean) => {
      if (!next.setActive(eligibility.current && visible)) return;
      const initialLocationTransition = locating.current;
      void next.start().then(() => {
        if (controller.current !== next || !next.isActive() || !next.snapshot.viewport) return;
        // Reuse any cached prices during the initial permission dialog and camera transition.
        // Later screen/app returns explicitly refresh current prices.
        if (initialLocationTransition) next.idle(next.snapshot.viewport, true);
        else void refresh();
      });
    };
    updateActivity.current = activity;
    const unsubscribeVisibility = subscribeToAppVisibility(activity);
    return () => {
      unsubscribeVisibility(); unsubscribe(); next.dispose();
      controller.current = null; updateActivity.current = null; refreshRequest.current = null;
      onForegroundRefreshChange?.(false);
    };
  }, [refresh, onForegroundRefreshChange]);
  useEffect(() => {
    authState.current = authenticated;
    controller.current?.setAuthenticated(authenticated);
  }, [authenticated]);
  useEffect(() => { locating.current = initialLocationPending; }, [initialLocationPending]);
  useEffect(() => {
    eligibility.current = eligible;
    updateActivity.current?.(isAppForeground());
  }, [eligible]);
  const onIdle = useCallback((viewport: Viewport) => { lastViewport.current = viewport; controller.current?.idle(viewport); }, []);
  const onMove = useCallback((viewport?: Viewport) => controller.current?.move(viewport), []);
  const metroBounds = useMemo(() => snapshot.index ? metroManilaBounds(snapshot.index) : undefined, [snapshot.index]);
  const locationCity = location?.city;
  const locationProvince = location?.province;
  const target = useMemo(() => snapshot.index && locationProvince !== undefined
    ? mapAreaForLocation(snapshot.index, { city: locationCity ?? '', province: locationProvince }) : null,
  [snapshot.index, locationCity, locationProvince]);
  const userCity = userLocation?.city;
  const userProvince = userLocation?.province;
  const userAreaBounds = useMemo(() => snapshot.index && userCity && userProvince
    ? mapAreaForLocation(snapshot.index, { city: userCity, province: userProvince })?.bbox : undefined,
    [snapshot.index, userCity, userProvince]);
  const latitude = userCoordinates?.latitude;
  const longitude = userCoordinates?.longitude;
  const indexBounds = snapshot.index?.bbox;
  const canLocate = latitude !== undefined && longitude !== undefined && Number.isFinite(latitude) && Number.isFinite(longitude) &&
    !!indexBounds && longitude >= indexBounds[0] && longitude <= indexBounds[2] && latitude >= indexBounds[1] && latitude <= indexBounds[3];
  const locationFocus = useMemo(() => canLocate ? { latitude: latitude!, longitude: longitude!, request: locationRequest, areaBounds: userAreaBounds } : null,
    [canLocate, latitude, longitude, locationRequest, userAreaBounds]);
  const zoomFocus = zoomCommand?.location === locationFocus ? zoomCommand.focus : null;
  const targetId = target?.id;
  const defaultCityId = !canLocate && metroBounds ? snapshot.index?.defaultCityId : undefined;
  const selectionOverride = mapSelection && mapSelection.targetId === targetId ? mapSelection : null;
  const selected = selectionOverride?.id ?? (selectionOverride?.id === null && defaultCityId ? defaultCityId : targetId) ?? defaultCityId ?? null;
  useEffect(() => {
    controller.current?.select(selected);
  }, [selected]);
  const onSelect = useCallback((id: string) => {
    setZoomCommand(null);
    setMapSelection({ id, targetId });
    if (!snapshot.index || !snapshot.viewport) return;
    const area = visibleAreas(snapshot.index, snapshot.viewport).find(value => value.id === id);
    if (!area || !authenticated) return;
    const city = !area.id.includes(':') ? area.members[0] : undefined;
    const known = locationForMapArea(area, locations);
    const prices = city && mapCache.price(city.id);
    if (city && (known || (prices && prices.status !== 'unavailable'))) {
      onLocationChange?.(known ?? { city: city.name.replace(/^(City|Municipality) of /i, ''),
        province: city.regionId === '1300000000' ? 'Metro Manila' : city.groupName }, city.id);
    } else if (area.id.startsWith('province:')) {
      onLocationChange?.(known ?? { city: '', province: area.name, scope: 'province' });
    }
  }, [authenticated, snapshot.index, snapshot.viewport, locations, onLocationChange, targetId]);
  const level = snapshot.index && snapshot.viewport ? mapLevel(snapshot.index, snapshot.viewport) : metroBounds ? 'city' : 'country';
  const noPrices = authenticated && !snapshot.loading && !snapshot.error && snapshot.prices.size > 0 &&
    [...snapshot.prices.values()].every(price => price.status === 'unavailable');
  const updatingMap = !!snapshot.index && snapshot.loading && !foregroundRefreshing;
  return <View style={styles.wrapper}>
    <ThemedText type="small" themeColor="textSecondary">Select a reported city or province to update the company list below. Broader areas show map ranges only.</ThemedText>
    <View style={styles.levelButtons} accessibilityLabel="Map geographic levels">
      {mapLevelButtons.map(item => <Pressable key={item.level} accessibilityRole="button"
        accessibilityLabel={`Show ${item.label.toLowerCase()}`} accessibilityHint={`Zoom the map to ${levelNames[item.level].toLowerCase()} level.`}
        accessibilityState={{ selected: !!snapshot.viewport && level === item.level, disabled: !snapshot.index || !snapshot.viewport }}
        disabled={!snapshot.index || !snapshot.viewport}
        onPress={() => {
          if (!snapshot.index || !snapshot.viewport) return;
          setZoomCommand({ location: locationFocus, focus: {
            zoom: zoomForLevel(snapshot.index, snapshot.viewport, item.level),
            ...(item.level === 'country' ? { center: cameraCenter(snapshot.index.bbox) } : {}),
          } });
        }}
        style={[styles.levelButton, !!snapshot.viewport && level === item.level && styles.levelButtonSelected, !snapshot.viewport && styles.levelButtonDisabled]}>
        <ThemedText type="smallBold" style={!!snapshot.viewport && level === item.level ? styles.levelButtonTextSelected : undefined}>{item.label}</ThemedText>
      </Pressable>)}
    </View>
    <ThemedText type="small" themeColor="textSecondary">{levelNames[level]} · Zoom or choose a level</ThemedText>
    {!canLocate && metroBounds && snapshot.viewport && <Pressable accessibilityRole="button" style={styles.metroButton}
      onPress={() => {
        if (!snapshot.index || !snapshot.viewport) return;
        setMapSelection({ id: null, targetId });
        setZoomCommand({ location: locationFocus, focus: defaultMapCamera(snapshot.index, snapshot.viewport, metroBounds) });
      }}>
      <ThemedText type="smallBold">Center on Metro Manila</ThemedText>
    </Pressable>}
    {showMapCredits && <Pressable accessibilityRole="button" accessibilityState={{ expanded: showCredits }}
      onPress={() => setShowCredits(value => !value)}>
      <ThemedText type="small" themeColor="textSecondary">Map credits {showCredits ? '↑' : '↓'}</ThemedText>
    </Pressable>}
    {showMapCredits && showCredits && <View style={styles.credits}>
      <Pressable accessibilityRole="link" accessibilityLabel="Boundary source and license" onPress={() => void Linking.openURL('https://github.com/faeldon/philippines-json-maps')}>
        <ThemedText style={styles.attribution}>Boundaries: NAMRIA / PSA through OCHA COD-AB · CC BY 3.0 IGO · James Faeldon · MIT. GasPriceApp regrouped city boundaries, normalized ring orientation, and merged and simplified area outlines.</ThemedText>
      </Pressable>
      <Pressable accessibilityRole="link" onPress={() => void Linking.openURL('https://creativecommons.org/licenses/by/3.0/igo/legalcode')}>
        <ThemedText style={styles.attribution}>Boundary license ↗</ThemedText>
      </Pressable>
      <ThemedText style={styles.attribution}>Location lookup uses Geoapify and OpenStreetMap contributors.</ThemedText>
    </View>}
    {!authenticated && !authLoading && <ThemedText type="small">Tap Reconnect above to load city prices.</ThemedText>}
    {snapshot.error && <View style={styles.error}><ThemedText type="small" accessibilityRole="alert">{snapshot.error}</ThemedText>
      <Pressable accessibilityRole="button" onPress={() => controller.current?.retry()} style={styles.retry}><ThemedText type="smallBold">Retry</ThemedText></Pressable></View>}
    <View style={styles.canvas}>
      {snapshot.index ? <MapRenderer snapshot={snapshot} selected={selected} focusBounds={target?.bbox} locationFocus={locationFocus} zoomFocus={zoomFocus} metroBounds={metroBounds} active={eligible && isAppForeground()} onMove={onMove} onIdle={onIdle} onSelect={onSelect} /> :
        <View style={styles.initial}><ActivityIndicator /><ThemedText>Loading Philippine map…</ThemedText></View>}
      {canLocate && <Pressable accessibilityRole="button" accessibilityLabel="Return to my location"
        accessibilityHint="Center the map on your detected location."
        onPress={() => { setZoomCommand(null); setLocationRequest(value => value + 1); onReturnToLocation?.(); }}
        style={styles.locationButton}>
        <AppIcon name="location" color="#76B4FF" />
      </Pressable>}
    </View>
    <View style={styles.mapStatus} accessibilityLiveRegion="polite">
      {updatingMap && <View style={styles.loading}>
        <ActivityIndicator size="small" />
        <ThemedText type="small">Updating map…</ThemedText>
      </View>}
      {/* Reserve the same space at every font size, including when the message wraps. */}
      <ThemedText type="small" aria-hidden={!noPrices} style={!noPrices && styles.hiddenStatus}>
        No city reports in this view in the last 30 days.
      </ThemedText>
    </View>
  </View>;
}
const styles = StyleSheet.create({
  levelButtons: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  levelButton: { minHeight: 44, paddingHorizontal: 14, paddingVertical: 10, borderWidth: 1, borderColor: '#495569', borderRadius: 12, justifyContent: 'center' },
  levelButtonSelected: { borderColor: '#76B4FF', backgroundColor: '#21334B' }, levelButtonTextSelected: { color: '#76B4FF' }, levelButtonDisabled: { opacity: 0.5 },
  metroButton: { alignSelf: 'flex-start', minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 10, borderWidth: 1, borderColor: '#495569' },
  locationButton: { position: 'absolute', left: 12, top: 12, width: 48, height: 48, borderRadius: 12, borderWidth: 1, borderColor: '#76B4FF', alignItems: 'center', justifyContent: 'center', backgroundColor: '#1A2029', zIndex: 20 },
  wrapper: { gap: 10 }, canvas: { height: 560, minHeight: 380, borderRadius: 14, overflow: 'hidden', backgroundColor: '#202832' },
  initial: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 12 }, credits: { gap: 8 },
  mapStatus: { minHeight: 44 },
  hiddenStatus: { opacity: 0 },
  loading: { position: 'absolute', top: 0, left: 0, right: 0, flexDirection: 'row', alignItems: 'center', gap: 8 },
  error: { gap: 6 },
  retry: { alignSelf: 'flex-start', padding: 10, borderRadius: 8, backgroundColor: '#293B50' },
  attribution: { color: '#98A8BB', fontSize: 10, lineHeight: 15 },
});
