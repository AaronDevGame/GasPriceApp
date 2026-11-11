import Constants, { ExecutionEnvironment } from 'expo-constants';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import MapView, { Marker, Polygon, PROVIDER_GOOGLE, type Region } from 'react-native-maps';

import { cameraBounds, fitZoom, MAX_ZOOM, midpoint, polygons } from './geometry';
import { areaIdentity, mapLevel } from './hierarchy';
import { placeLabels, rangeText } from './labels';
import type { MapRendererProps, Point } from './types';

const coordinate = ([longitude, latitude]: Point) => ({ latitude, longitude });
export default function MapRenderer(props: MapRendererProps) {
  const [attempt, setAttempt] = useState(0);
  const configured = Constants.expoConfig?.extra?.fuelMap?.[Platform.OS === 'ios' ? 'iosConfigured' : 'androidConfigured'];
  if (!configured || (Platform.OS === 'ios' && Constants.executionEnvironment === ExecutionEnvironment.StoreClient)) {
    return <View style={styles.unavailable}><Text style={styles.notice}>The map is not available in this build.</Text></View>;
  }
  return <NativeMap key={attempt} {...props} onRetry={() => setAttempt(value => value + 1)} />;
}
function NativeMap({ snapshot, selected, onMove, onIdle, onSelect, onRetry }: MapRendererProps & { onRetry(): void }) {
  const index = snapshot.index!;
  const map = useRef<MapView | null>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [ready, setReady] = useState(false);
  const [moving, setMoving] = useState(true);
  const sequence = useRef(0);
  const initialized = useRef(false);
  const [failure, setFailure] = useState(false);
  const bounds = useMemo(() => cameraBounds(index.bbox), [index]);
  const center = useMemo(() => midpoint(index.bbox), [index]);
  const minZoom = size.width ? fitZoom(index.bbox, size.width, size.height, 35) : 4;
  const start = useCallback((region: Region) => {
    ++sequence.current; setMoving(true); onMove();
    const latitude = Math.max(bounds[1], Math.min(bounds[3], region.latitude));
    const longitude = Math.max(bounds[0], Math.min(bounds[2], region.longitude));
    // SDK 57 uses Fabric; the library's legacy setMapBoundaries can be a no-op there.
    // Enforce center bounds independently during movement, then verify camera/zoom at idle.
    if (Math.abs(latitude - region.latitude) > 0.00001 || Math.abs(longitude - region.longitude) > 0.00001) {
      map.current?.setCamera({ center: { latitude, longitude }, pitch: 0, heading: 0 });
    }
  }, [bounds, onMove]);
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
      setFailure(false); setMoving(false);
      onIdle({ bounds: [visible.southWest.longitude, visible.southWest.latitude, visible.northEast.longitude, visible.northEast.latitude],
        zoom, ...size });
    } catch { setFailure(true); }
  }, [bounds, minZoom, size, onIdle]);
  useEffect(() => {
    if (!ready || !size.width || !map.current) return;
    const frame = requestAnimationFrame(() => {
      try {
        // Google native bounds constrain the center; min zoom independently limits the overview.
        map.current?.setMapBoundaries(coordinate([bounds[2], bounds[3]]), coordinate([bounds[0], bounds[1]]));
        if (!initialized.current) {
          initialized.current = true;
          map.current?.setCamera({ center: coordinate(center), zoom: fitZoom(index.bbox, size.width, size.height, 35), pitch: 0, heading: 0 });
        }
        void settle();
      } catch { setFailure(true); }
    });
    return () => cancelAnimationFrame(frame);
  }, [ready, size, bounds, center, index, settle]);
  useEffect(() => () => { ++sequence.current; }, []);
  useEffect(() => {
    if (ready) return;
    const timeout = setTimeout(() => setFailure(true), 25000);
    return () => clearTimeout(timeout);
  }, [ready]);
  const level = snapshot.viewport ? mapLevel(index, snapshot.viewport) : 'country';
  const ids = useMemo(() => new Map(index.cities.map(city => [city.id, areaIdentity(city, level)[0]])), [index, level]);
  const shapes = useMemo(() => snapshot.features.flatMap(feature => polygons(feature.geometry).map((rings, part) => ({
    id: ids.get(feature.id) ?? feature.id, key: feature.id, part, outer: rings[0].map(coordinate), holes: rings.slice(1).map(ring => ring.map(coordinate)),
  }))), [snapshot.features, ids]);
  const labels = moving ? [] : placeLabels(snapshot, selected);
  return <View style={styles.fill} onLayout={event => {
    const { width, height } = event.nativeEvent.layout;
    setSize(old => old.width === width && old.height === height ? old : { width, height });
  }}>
    <MapView ref={map} provider={PROVIDER_GOOGLE} style={styles.fill}
      initialRegion={{ ...coordinate(center), latitudeDelta: index.bbox[3] - index.bbox[1], longitudeDelta: index.bbox[2] - index.bbox[0] }}
      minZoomLevel={minZoom} maxZoomLevel={MAX_ZOOM} rotateEnabled={false} pitchEnabled={false}
      toolbarEnabled={false} showsPointsOfInterests={false} showsBuildings={false}
      onMapReady={() => setReady(true)} onRegionChange={start} onRegionChangeComplete={() => void settle()}>
      {shapes.map(shape => <Polygon key={`${shape.key}:${shape.part}`} coordinates={shape.outer} holes={shape.holes}
        tappable onPress={() => onSelect(shape.id)} strokeWidth={level === 'city' ? (shape.id === selected ? 2.5 : 1) : 0}
        strokeColor={shape.id === selected ? '#D38B00' : '#3F83BE'} fillColor={shape.id === selected ? '#FFD16655' : '#348EF020'} />)}
      {labels.map((label, priority) => <Marker zIndex={labels.length - priority} key={`${label.id}:${label.fontSize}:${label.name}:${rangeText(label.price.gasoline)}:${rangeText(label.price.diesel)}:${label.coverage}:${selected === label.id}`}
        coordinate={coordinate(label.anchor)} anchor={{ x: 0.5, y: 0.5 }} tracksViewChanges={false}
        onPress={() => onSelect(label.id)} accessibilityLabel={`${label.name}, gasoline RON 91 ${rangeText(label.price.gasoline)}, diesel ${rangeText(label.price.diesel)}, ${label.coverage}`}>
        <View style={[styles.label, { width: label.width, height: label.height, borderColor: label.id === selected ? '#FFD166' : '#6CB4F8' }]}>
          <Text numberOfLines={1} allowFontScaling={false} style={[styles.price, { fontSize: label.fontSize, lineHeight: label.fontSize + 4 }]}>{label.name}</Text>
          <Text allowFontScaling={false} style={[styles.price, { fontSize: label.fontSize, lineHeight: label.fontSize + 4 }]}>{rangeText(label.price.gasoline)}</Text>
          <Text allowFontScaling={false} style={[styles.price, { fontSize: label.fontSize, lineHeight: label.fontSize + 4 }]}>{rangeText(label.price.diesel)}</Text>
          <Text allowFontScaling={false} style={[styles.price, { fontSize: 10 }]}>{label.coverage}</Text>
        </View>
      </Marker>)}
    </MapView>
    {failure && <View style={styles.warning}><Text style={styles.notice}>The map could not update. Check your connection.</Text>
      <Pressable accessibilityRole="button" onPress={onRetry} style={styles.retry}><Text style={styles.notice}>Retry map</Text></Pressable>
    </View>}
  </View>;
}
const styles = StyleSheet.create({
  fill: { flex: 1 }, unavailable: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  notice: { color: '#E8EDF5', textAlign: 'center' }, warning: { position: 'absolute', top: 10, left: 12, right: 12, padding: 10, backgroundColor: '#243142' },
  label: { height: 46, paddingVertical: 5, borderRadius: 7, backgroundColor: '#172332', alignItems: 'center', justifyContent: 'center', borderWidth: 1, borderColor: '#6CB4F8' },
  retry: { padding: 10, marginTop: 6, borderRadius: 8, backgroundColor: '#354B64' },
  price: { fontSize: 11, lineHeight: 17, color: '#FFFFFF', fontWeight: '600', fontVariant: ['tabular-nums'] },
});
