import Constants, { ExecutionEnvironment } from 'expo-constants';
import { Image } from 'expo-image';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import MapView, { Polygon, PROVIDER_GOOGLE, type Region } from 'react-native-maps';

import { fuelNozzles } from '@/fuel/nozzles';

import { boundsCenter, cameraBounds, cameraCenter, fitZoom, FUEL_CARD_PAN_DURATION_MS, intersects, MAX_ZOOM, polygons, project } from './geometry';
import { areaIdentity, defaultMapCamera, mapLevel } from './hierarchy';
import { clampLabelCenter, edgeLabelPosition, edgeMarker, edgeMarkerStatus, mapLabelColors, mapLabelNozzle, placeLabels } from './labels';
import type { Bounds, MapRendererProps, Point, Viewport } from './types';

const coordinate = ([longitude, latitude]: Point) => ({ latitude, longitude });
function ringBounds(ring: Point[]): Bounds {
  const bounds: Bounds = [Infinity, Infinity, -Infinity, -Infinity];
  for (const [lng, lat] of ring) {
    bounds[0] = Math.min(bounds[0], lng);
    bounds[1] = Math.min(bounds[1], lat);
    bounds[2] = Math.max(bounds[2], lng);
    bounds[3] = Math.max(bounds[3], lat);
  }
  return bounds;
}
export default function MapRenderer(props: MapRendererProps) {
  const [attempt, setAttempt] = useState(0);
  const configured = Constants.expoConfig?.extra?.fuelMap?.[Platform.OS === 'ios' ? 'iosConfigured' : 'androidConfigured'];
  if (!configured || (Platform.OS === 'ios' && Constants.executionEnvironment === ExecutionEnvironment.StoreClient)) {
    return <View style={styles.unavailable}><Text style={styles.notice}>The map is not available in this build.</Text></View>;
  }
  return <NativeMap key={attempt} {...props} onRetry={() => setAttempt(value => value + 1)} />;
}
function NativeMap({ snapshot, selected, focusBounds, locationFocus, zoomFocus, metroBounds, active, onMove, onIdle, onSelect, onRetry }: MapRendererProps & { onRetry(): void }) {
  const index = snapshot.index!;
  const map = useRef<MapView | null>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [ready, setReady] = useState(false);
  const [liveBounds, setLiveBounds] = useState<Bounds | null>(null);
  const pendingBounds = useRef<Bounds | null>(null);
  const labelFrame = useRef<number | null>(null);
  const sequence = useRef(0);
  const initialized = useRef(false);
  const focusedZoom = useRef<MapRendererProps['zoomFocus']>(null);
  const focusedLocation = useRef<MapRendererProps['locationFocus']>(null);
  const focusedBounds = useRef<Bounds | null>(null);
  const [failure, setFailure] = useState(false);
  const bounds = useMemo(() => cameraBounds(index.bbox), [index]);
  const initialBounds = metroBounds ?? index.bbox;
  const center = useMemo(() => metroBounds ? boundsCenter(metroBounds) : cameraCenter(index.bbox), [metroBounds, index]);
  const minZoom = size.width ? fitZoom(bounds, size.width, size.height, 0) : 0;
  const cancelLabelFrame = useCallback(() => {
    if (labelFrame.current !== null) cancelAnimationFrame(labelFrame.current);
    labelFrame.current = null;
  }, []);
  const start = useCallback((region: Region) => {
    ++sequence.current;
    let viewport: Viewport | undefined;
    if (region.latitudeDelta > 0 && region.longitudeDelta > 0) {
      const halfWidth = region.longitudeDelta / 2, halfHeight = region.latitudeDelta / 2;
      pendingBounds.current = [region.longitude - halfWidth, region.latitude - halfHeight,
        region.longitude + halfWidth, region.latitude + halfHeight];
      labelFrame.current ??= requestAnimationFrame(() => {
        labelFrame.current = null;
        setLiveBounds(pendingBounds.current);
      });
      if (size.width && size.height) {
        viewport = { bounds: pendingBounds.current, zoom: fitZoom(pendingBounds.current, size.width, size.height, 0), ...size };
      }
    }
    onMove(viewport);
    const latitude = Math.max(bounds[1], Math.min(bounds[3], region.latitude));
    const longitude = Math.max(bounds[0], Math.min(bounds[2], region.longitude));
    // SDK 57 uses Fabric; the library's legacy setMapBoundaries can be a no-op there.
    // Enforce center bounds independently during movement, then verify camera/zoom at idle.
    if (Math.abs(latitude - region.latitude) > 0.00001 || Math.abs(longitude - region.longitude) > 0.00001) {
      map.current?.setCamera({ center: { latitude, longitude }, pitch: 0, heading: 0 });
    }
  }, [bounds, onMove, size]);
  const settle = useCallback(async () => {
    if (!map.current || !size.width) return;
    const ticket = ++sequence.current;
    try {
      const [visible, camera] = await Promise.all([map.current.getMapBoundaries(), map.current.getCamera()]);
      if (ticket !== sequence.current) return;
      const latitude = Math.max(bounds[1], Math.min(bounds[3], camera.center.latitude));
      const longitude = Math.max(bounds[0], Math.min(bounds[2], camera.center.longitude));
      const zoom = Math.max(minZoom, Math.min(MAX_ZOOM, camera.zoom ?? minZoom));
      if (Math.abs(latitude - camera.center.latitude) > 0.00001 || Math.abs(longitude - camera.center.longitude) > 0.00001 ||
          Math.abs(zoom - (camera.zoom ?? zoom)) > 0.05) {
        map.current.setCamera({ center: { latitude, longitude }, zoom, pitch: 0, heading: 0 });
        return;
      }
      cancelLabelFrame();
      setFailure(false);
      onIdle({ bounds: [visible.southWest.longitude, visible.southWest.latitude, visible.northEast.longitude, visible.northEast.latitude],
        zoom, ...size });
      setLiveBounds(null);
    } catch { setFailure(true); }
  }, [bounds, minZoom, size, onIdle, cancelLabelFrame]);
  useEffect(() => {
    if (!ready || !size.width || !map.current) return;
    const frame = requestAnimationFrame(() => {
      try {
        // Google native bounds constrain the center; min zoom independently limits how far out users can browse.
        map.current?.setMapBoundaries(coordinate([bounds[2], bounds[3]]), coordinate([bounds[0], bounds[1]]));
        if (!initialized.current) {
          initialized.current = true;
          const camera = defaultMapCamera(index, size, metroBounds);
          map.current?.setCamera({ center: coordinate(camera.center), zoom: camera.zoom, pitch: 0, heading: 0 });
        }
        void settle();
      } catch { setFailure(true); }
    });
    return () => cancelAnimationFrame(frame);
  }, [ready, size, bounds, metroBounds, index, settle]);
  useEffect(() => {
    if (!ready || !size.width || active === false || !focusBounds || focusedBounds.current === focusBounds) return;
    const frame = requestAnimationFrame(() => {
      const [longitude, latitude] = boundsCenter(focusBounds);
      map.current?.setCamera({ center: { latitude, longitude }, zoom: Math.min(MAX_ZOOM, fitZoom(focusBounds, size.width, size.height, 36)), pitch: 0, heading: 0 });
      focusedBounds.current = focusBounds;
    });
    return () => cancelAnimationFrame(frame);
  }, [ready, size, focusBounds, active]);
  useEffect(() => {
    if (!ready || !size.width || active === false || !locationFocus || focusedLocation.current === locationFocus) return;
    const frame = requestAnimationFrame(() => {
      const [longitude, latitude] = locationFocus.areaBounds ? boundsCenter(locationFocus.areaBounds) : [locationFocus.longitude, locationFocus.latitude];
      map.current?.setCamera({ center: { latitude, longitude }, zoom: 12, pitch: 0, heading: 0 });
      focusedLocation.current = locationFocus;
    });
    return () => cancelAnimationFrame(frame);
  }, [ready, size, locationFocus, active]);
  useEffect(() => {
    if (!ready || !size.width || active === false || !zoomFocus || focusedZoom.current === zoomFocus) return;
    const frame = requestAnimationFrame(() => {
      map.current?.setCamera({ zoom: zoomFocus.zoom, ...(zoomFocus.center ? { center: coordinate(zoomFocus.center) } : {}) });
      focusedZoom.current = zoomFocus;
    });
    return () => cancelAnimationFrame(frame);
  }, [ready, size, zoomFocus, active]);
  useEffect(() => () => { ++sequence.current; cancelLabelFrame(); }, [cancelLabelFrame]);
  useEffect(() => {
    if (ready) return;
    const timeout = setTimeout(() => setFailure(true), 25000);
    return () => clearTimeout(timeout);
  }, [ready]);
  const level = snapshot.viewport ? mapLevel(index, snapshot.viewport) : 'country';
  const ids = useMemo(() => new Map(index.cities.map(city => [city.id, areaIdentity(city, level)[0]])), [index, level]);
  const shapes = useMemo(() => snapshot.features.flatMap(feature => {
    const areaId = ids.get(feature.id) ?? feature.id;
    return polygons(feature.geometry).map((rings, part) => ({
      key: `${feature.id}:${part}`, areaId, hasOutline: areaId === feature.id, bbox: ringBounds(rings[0]),
      outer: rings[0].map(coordinate), holes: rings.slice(1).map(ring => ring.map(coordinate)),
    }));
  }), [snapshot.features, ids]);
  // Native Maps needs one overlay per separate island. Mount only the visible parts.
  const visibleBounds = snapshot.viewport?.bounds;
  const polygonOverlays = useMemo(() => shapes.filter(shape => visibleBounds && intersects(shape.bbox, visibleBounds)).map(shape => {
    const highlighted = shape.areaId === selected;
    return <Polygon key={shape.key} coordinates={shape.outer} holes={shape.holes}
      tappable onPress={() => onSelect(shape.areaId)} strokeWidth={shape.hasOutline ? (highlighted ? 2.5 : 1) : 0}
      zIndex={highlighted ? 1 : 0} strokeColor={highlighted ? '#D38B00' : '#3F83BE'}
      fillColor={highlighted ? '#FFD16655' : '#348EF020'} />;
  }), [shapes, visibleBounds, selected, onSelect]);
  // Keep geographic anchors moving between viewport updates from the controller.
  const labels = useMemo(() => placeLabels(snapshot, selected), [snapshot, selected]);
  const liveViewport = liveBounds && snapshot.viewport ? { ...snapshot.viewport, ...size, bounds: liveBounds } : null;
  const labelViewport = liveViewport ?? snapshot.viewport;
  return <View style={styles.fill} onLayout={event => {
    const { width, height } = event.nativeEvent.layout;
    setSize(old => old.width === width && old.height === height ? old : { width, height });
  }}>
    <MapView ref={map} provider={PROVIDER_GOOGLE} style={styles.fill}
      initialRegion={{ ...coordinate(center), latitudeDelta: initialBounds[3] - initialBounds[1], longitudeDelta: initialBounds[2] - initialBounds[0] }}
      minZoomLevel={minZoom} maxZoomLevel={MAX_ZOOM} rotateEnabled={false} pitchEnabled={false}
      toolbarEnabled={false} showsPointsOfInterests={false} showsBuildings={false}
      onMapReady={() => setReady(true)} onRegionChange={start} onRegionChangeComplete={() => void settle()}>
      {polygonOverlays}
    </MapView>
    <View pointerEvents="box-none" style={StyleSheet.absoluteFill}>
      {labels.map((label, priority) => {
        if (!labelViewport || (liveViewport && !intersects(label.bounds, liveViewport.bounds))) return null;
        const { width: cardWidth, height: cardHeight } = label;
        const [x, y] = liveViewport
          ? clampLabelCenter(project(label.anchor, liveViewport), liveViewport, cardWidth, cardHeight)
          : [label.x, label.y];
        const regular = { x, y, width: cardWidth, height: cardHeight };
        const edge = edgeLabelPosition(label, project(label.labelPoint, labelViewport), regular, labelViewport);
        const position = edge ?? regular;
        const { width, height } = position;
        const left = position.x - width / 2, top = position.y - height / 2;
        const highlighted = label.id === selected;
        const hasPrices = label.priceRows.length > 0;
        const pending = label.skeletonWidths.length > 0;
        const colors = highlighted ? mapLabelColors.selected : pending ? mapLabelColors.skeleton : mapLabelColors.neutral;
        const muted = !highlighted && !hasPrices && !pending;
        const priceStyle = [styles.price, edge
          ? { fontSize: edgeMarker.fontSize, lineHeight: edgeMarker.lineHeight }
          : { fontSize: label.fontSize, lineHeight: label.fontSize + 4 }];
        const coverage = edge ? (hasPrices ? '' : edgeMarkerStatus) : label.coverage;
        return <Pressable key={label.id} accessibilityRole="button" accessibilityLabel={label.accessibilityLabel}
          accessibilityState={{ busy: pending, selected: highlighted }}
          onPress={() => {
            map.current?.animateCamera({ center: coordinate(label.center) }, { duration: FUEL_CARD_PAN_DURATION_MS });
            onSelect(label.id);
          }}
          style={[styles.label, edge && styles.edgeLabel, { position: 'absolute', left, top,
            zIndex: (edge ? 1 : 2) * labels.length - priority,
            width, height, backgroundColor: colors.background, borderColor: colors.border }]}>
          {edge && <View pointerEvents="none" style={[StyleSheet.absoluteFill, { transform: [{ rotate: `${edge.tailAngle}rad` }] }]}>
            <View style={[styles.edgeTail, { borderLeftColor: colors.border }]} />
          </View>}
          <Text numberOfLines={1} allowFontScaling={false} style={[priceStyle, edge && styles.edgeName,
            muted && styles.unavailableText]}>{edge ? label.edgeName : label.name}</Text>
          {pending ? label.skeletonWidths.map(width => (
            <View key={width} style={[styles.skeletonRow, edge && styles.edgeSkeletonRow]}><View style={[styles.skeletonBar, { width: `${width}%` }]} /></View>
          )) : <>
            {hasPrices && <View style={edge ? styles.edgePrices : styles.priceRows}>
              {label.priceRows.map(({ fuel, text, edgeText }) => (
                <View key={fuel} style={edge ? [styles.edgeColumn, fuel === 'diesel' && styles.edgeDiesel] : styles.priceRow}>
                  <Image source={fuelNozzles[fuel]} style={edge ? styles.edgeNozzle : styles.nozzle} contentFit="contain" accessible={false} />
                  <Text allowFontScaling={false} style={priceStyle}>{edge ? edgeText : text}</Text>
                </View>
              ))}
            </View>}
            {!!coverage && <Text allowFontScaling={false} style={[styles.price, styles.coverage, edge && styles.edgeCoverage,
              muted && styles.unavailableText, !hasPrices && styles.noReport]}>{coverage}</Text>}
          </>}
        </Pressable>;
      })}
    </View>
    {failure && <View style={styles.warning}><Text style={styles.notice}>The map could not update. Check your connection.</Text>
      <Pressable accessibilityRole="button" onPress={onRetry} style={styles.retry}><Text style={styles.notice}>Retry map</Text></Pressable>
    </View>}
  </View>;
}
const styles = StyleSheet.create({
  fill: { flex: 1 }, unavailable: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  notice: { color: '#E8EDF5', textAlign: 'center' }, warning: { position: 'absolute', top: 10, left: 12, right: 12, padding: 10, backgroundColor: '#243142' },
  label: { height: 46, paddingVertical: 5, borderRadius: 7, alignItems: 'center', justifyContent: 'center', borderWidth: 1 },
  edgeLabel: { borderRadius: edgeMarker.diameter / 2, paddingHorizontal: edgeMarker.padding },
  edgeName: { fontSize: edgeMarker.nameFontSize },
  edgeTail: {
    position: 'absolute', width: 0, height: 0, left: edgeMarker.diameter - edgeMarker.tailOverlap - 1,
    top: edgeMarker.diameter / 2 - edgeMarker.tailHalfWidth - 1, borderLeftWidth: edgeMarker.tailLength,
    borderTopWidth: edgeMarker.tailHalfWidth, borderBottomWidth: edgeMarker.tailHalfWidth,
    borderTopColor: 'transparent', borderBottomColor: 'transparent',
  },
  skeletonRow: { height: 16, alignSelf: 'stretch', alignItems: 'center', justifyContent: 'center' },
  edgeSkeletonRow: { height: 10 },
  skeletonBar: { height: 7, borderRadius: 4, backgroundColor: mapLabelColors.skeleton.bar },
  unavailableText: { color: mapLabelColors.unavailableText },
  noReport: { fontStyle: 'italic' },
  coverage: { fontSize: 10, textAlign: 'center' },
  edgeCoverage: { fontSize: edgeMarker.fontSize, lineHeight: edgeMarker.lineHeight },
  retry: { padding: 10, marginTop: 6, borderRadius: 8, backgroundColor: '#354B64' },
  priceRows: { alignItems: 'center' },
  priceRow: { flexDirection: 'row', alignItems: 'center', gap: mapLabelNozzle.gap },
  edgePrices: { flexDirection: 'row', width: 2 * edgeMarker.columnWidth },
  edgeColumn: { width: edgeMarker.columnWidth, alignItems: 'center' },
  edgeDiesel: { marginLeft: 'auto' },
  edgeNozzle: { width: edgeMarker.nozzleSize, height: edgeMarker.nozzleSize },
  nozzle: { width: mapLabelNozzle.size, height: mapLabelNozzle.size },
  price: { fontSize: 11, lineHeight: 17, color: '#FFFFFF', fontWeight: '600', fontVariant: ['tabular-nums'],
    textShadowColor: mapLabelColors.textShadow, textShadowOffset: { width: 0, height: 1 }, textShadowRadius: 3 },
});
