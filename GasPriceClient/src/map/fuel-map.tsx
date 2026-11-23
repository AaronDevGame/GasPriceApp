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
import { areaPrices, defaultMapCamera, levelNames, mapLevel, mapLevelButtons, metroManilaBounds, zoomForLevel, visibleAreas } from './hierarchy';
import { cameraCenter } from './geometry';
import { adjustmentSummaries, priceLabel, rangeText, reportDate } from './labels';
import { mapAreaForLocation, locationForMapArea } from '@/fuel/selection';
import type { Coordinates, DoePriceLocation } from '@/fuel/api';
import type { LocationFocus, MapSnapshot, Viewport, ZoomFocus } from './types';

const empty: MapSnapshot = { index: null, cities: [], features: [], prices: new Map(), viewport: null, loading: true, detail: false, error: null };
export type FuelMapHandle = { refresh(): Promise<void> };

export default function FuelMap({ ref, foregroundRefreshing = false, onForegroundRefreshChange, location, userLocation, userCoordinates, onReturnToLocation, locations = [], onLocationChange, grade, active = true, initialLocationPending = false }: {
  location?: DoePriceLocation | null;
  userCoordinates?: Coordinates | null;
  userLocation?: DoePriceLocation | null;
  onReturnToLocation?(): void;
  locations?: DoePriceLocation[];
  onLocationChange?(location: DoePriceLocation): void;
  grade?: string;
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
  const onMove = useCallback(() => controller.current?.move(), []);
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
    const next = area && locationForMapArea(area, locations);
    if (next) onLocationChange?.(next);
  }, [snapshot.index, snapshot.viewport, locations, onLocationChange, targetId]);
  const level = snapshot.index && snapshot.viewport ? mapLevel(snapshot.index, snapshot.viewport) : metroBounds ? 'city' : 'country';
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
    <View style={styles.status}>
      {selection && prices && <View style={styles.selection}>
        <ThemedText type="smallBold">{selection.name}</ThemedText>
        <ThemedText type="smallBold">{priceLabel(prices)}</ThemedText>
        <ThemedText type="small">RON 91 {rangeText(prices.gasoline)} · Diesel {rangeText(prices.diesel)}</ThemedText>
        {prices.report?.weekStart && prices.report.weekEnd && <ThemedText type="small" themeColor="textSecondary">
          DOE report: {reportDate(prices.report.weekStart)}–{reportDate(prices.report.weekEnd)}
        </ThemedText>}
        {prices.report?.priceKind === 'estimated' && <>
          <ThemedText type="small">Original DOE reported: RON 91 {rangeText(prices.report.reportedGasoline ?? null)} · Diesel {rangeText(prices.report.reportedDiesel ?? null)}</ThemedText>
          {adjustmentSummaries(prices.report.adjustments ?? []).map((change, i) => <ThemedText key={i} type="small" themeColor="textSecondary">
            {reportDate(change.date)} · {change.fuel === 'gasoline' ? 'RON 91' : 'Diesel'} {change.amount >= 0 ? '+' : '−'}₱{Math.abs(change.amount).toFixed(2)}/L · {[...change.companies].join(', ')}
          </ThemedText>)}
          <ThemedText type="small" themeColor="textSecondary">Estimated from the DOE baseline and confirmed effective adjustments. Actual station prices may differ.</ThemedText>
        </>}
        {prices.report?.isOutdated && <ThemedText type="small" themeColor="textSecondary">Last DOE-reported prices. A complete set of confirmed adjustments is unavailable; current pump prices may differ.</ThemedText>}
        {[...new Set([...(prices.report?.sourceUrls ?? []), ...(prices.report?.adjustments ?? []).flatMap(change => change.sourceUrls)])]
          .map((url, i) => <Pressable key={url} accessibilityRole="link" onPress={() => void Linking.openURL(url)}><ThemedText type="small" themeColor="textSecondary">{i === 0 ? 'View DOE report' : `Adjustment source ${i}`} ↗</ThemedText></Pressable>)}
        {level !== 'city' && <ThemedText type="small">{prices.reported} of {prices.total} cities / municipalities with prices · {prices.estimated} estimated · {prices.outdated} outdated</ThemedText>}
      </View>}
      {snapshot.loading && !foregroundRefreshing && <View style={styles.loading}><ActivityIndicator size="small" /><ThemedText type="small">Updating map…</ThemedText></View>}
      {!authenticated && !authLoading && <ThemedText type="small">Tap Reconnect above to load city prices.</ThemedText>}
      {noPrices && <ThemedText type="small">No city reports from the last 30 days are available in this view.</ThemedText>}
      {snapshot.error && <View style={styles.error}><ThemedText type="small" accessibilityRole="alert">{snapshot.error}</ThemedText>
        <Pressable accessibilityRole="button" onPress={() => controller.current?.retry()} style={styles.retry}><ThemedText type="smallBold">Retry</ThemedText></Pressable></View>}
      {/* Fuel descriptions belong outside the map; overlays show the location above the two ranges. */}
      <ThemedText type="small" themeColor="textSecondary">Top: gasoline RON 91 · Bottom: diesel · PHP per liter · — unavailable</ThemedText>
      <ThemedText type="small" themeColor="textSecondary">DOE reported = original report prices. Estimated = DOE baseline plus confirmed adjustments. Outdated = report ended over 7 days ago. Reports over 30 days old are hidden. Area ranges combine city minima and maxima; station prices vary.</ThemedText>
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
  metroButton: { alignSelf: 'flex-start', minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 10, borderWidth: 1, borderColor: '#495569' },
  locationButton: { position: 'absolute', left: 12, top: 12, width: 48, height: 48, borderRadius: 12, borderWidth: 1, borderColor: '#76B4FF', alignItems: 'center', justifyContent: 'center', backgroundColor: '#1A2029', zIndex: 20 },
  selection: { padding: 12, borderRadius: 10, borderWidth: 1, borderColor: '#D38B00', gap: 4 },
  wrapper: { gap: 10 }, canvas: { height: 560, minHeight: 380, borderRadius: 14, overflow: 'hidden', backgroundColor: '#202832' },
  initial: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 12 }, status: { gap: 8 },
  loading: { flexDirection: 'row', alignItems: 'center', gap: 8 }, error: { gap: 6 },
  retry: { alignSelf: 'flex-start', padding: 10, borderRadius: 8, backgroundColor: '#293B50' },
  attribution: { color: '#98A8BB', fontSize: 10, lineHeight: 15 },
});
