import { useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { cameraBounds, fitZoom, MAX_ZOOM, polygons } from './geometry';
import { areaIdentity, mapLevel } from './hierarchy';
import { loadGoogleMaps } from './google-loader.web';
import { type MapLabel, placeLabels, rangeText } from './labels';
import type { Bounds, MapRendererProps } from './types';

const literalBounds = (bounds: Bounds) => ({ west: bounds[0], south: bounds[1], east: bounds[2], north: bounds[3] });
type LabelOverlay = google.maps.OverlayView & { labels: MapLabel[]; selected: string | null };
function createLabels(onSelect: (id: string) => void) {
      class Labels extends google.maps.OverlayView {
        labels: MapLabel[] = [];
        selected: string | null = null;
        private root = document.createElement('div');
        onAdd() { this.root.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;'; this.getPanes()?.overlayMouseTarget.appendChild(this.root); }
        draw() {
          const projection = this.getProjection();
          if (!projection) return;
          this.root.replaceChildren(...this.labels.map((label, priority) => {
            const point = projection.fromLatLngToDivPixel(new google.maps.LatLng(label.anchor[1], label.anchor[0]));
            const button = document.createElement('button');
            button.type = 'button';
            button.setAttribute('aria-label', `${label.name}, gasoline RON 91 ${rangeText(label.price.gasoline)}, diesel ${rangeText(label.price.diesel)}, ${label.coverage}`);
            button.style.cssText = `box-sizing:border-box;padding:4px;overflow:hidden;position:absolute;z-index:${this.labels.length - priority};left:${point?.x ?? 0}px;top:${point?.y ?? 0}px;transform:translate(-50%,-50%);width:${label.width}px;height:${label.height}px;background:#172332;color:white;border:2px solid ${label.id === this.selected ? '#FFD166' : '#6CB4F8'};border-radius:7px;font:600 ${label.fontSize}px/16px system-ui;white-space:pre;text-align:center;cursor:pointer;pointer-events:auto;font-variant-numeric:tabular-nums;`;
            button.textContent = [label.name, rangeText(label.price.gasoline), rangeText(label.price.diesel), label.coverage].filter(Boolean).join('\n');
            button.onclick = () => onSelect(label.id);
            google.maps.OverlayView.preventMapHitsAndGesturesFrom(button);
            return button;
          }));
        }
        onRemove() { this.root.remove(); }
      }
  return new Labels();
}
export default function MapRenderer(props: MapRendererProps) {
  const host = useRef<HTMLDivElement | null>(null);
  const latest = useRef(props);
  const map = useRef<google.maps.Map | null>(null);
  const shapes = useRef<google.maps.Polygon[]>([]);
  const overlay = useRef<LabelOverlay | null>(null);
  const [moving, setMoving] = useState(true);
  const [ready, setReady] = useState(false);
  const [error, setError] = useState(false);
  const [retry, setRetry] = useState(0);
  const key = process.env.EXPO_PUBLIC_GOOGLE_MAPS_WEB_API_KEY;
  const version = props.snapshot.index!.version;
  useEffect(() => { latest.current = props; }, [props]);
  useEffect(() => {
    if (!key || !host.current) return;
    let active = true;
    let resize: ResizeObserver | undefined;
    const listeners: google.maps.MapsEventListener[] = [];
    const callbacks = window as unknown as { gm_authFailure?: () => void };
    const previousAuthFailure = callbacks.gm_authFailure;
    const authFailure = () => { if (active) setError(true); previousAuthFailure?.(); };
    callbacks.gm_authFailure = authFailure;
    setError(false); setReady(false);
    void loadGoogleMaps(key).then(() => {
      if (!active || !host.current) return;
      const index = latest.current.snapshot.index!;
      const bounds = cameraBounds(index.bbox);

      const instance = new google.maps.Map(host.current, {
        center: { lat: 12.8, lng: 122 }, zoom: 5, maxZoom: MAX_ZOOM,
        restriction: { latLngBounds: literalBounds(bounds), strictBounds: false },
        mapTypeControl: false, streetViewControl: false, fullscreenControl: false,
        rotateControl: false, tilt: 0, heading: 0, gestureHandling: 'greedy',
        styles: [{ featureType: 'poi', stylers: [{ visibility: 'off' }] }, { featureType: 'transit', stylers: [{ visibility: 'off' }] }],
      });
      map.current = instance;
      const labels = createLabels(id => latest.current.onSelect(id)); labels.setMap(instance); overlay.current = labels;
      listeners.push(instance.addListener('bounds_changed', () => {
        latest.current.onMove(); setMoving(true); labels.labels = []; labels.draw();
      }));
      listeners.push(instance.addListener('idle', () => {
        const visible = instance.getBounds();
        if (!visible || !host.current || !active) return;
        const sw = visible.getSouthWest(), ne = visible.getNorthEast();
        setMoving(false);
        latest.current.onIdle({ bounds: [sw.lng(), sw.lat(), ne.lng(), ne.lat()], zoom: instance.getZoom() ?? 12,
          width: host.current.clientWidth, height: host.current.clientHeight });
      }));
      const updateSize = () => {
        if (!host.current?.clientWidth) return;
        instance.setOptions({ minZoom: fitZoom(index.bbox, host.current.clientWidth, host.current.clientHeight, 35) });
      };
      resize = new ResizeObserver(updateSize); resize.observe(host.current); updateSize();
      instance.fitBounds(literalBounds(index.bbox), 35);
      setReady(true);
    }).catch(() => { if (active) setError(true); });
    return () => {
      active = false; resize?.disconnect(); listeners.forEach(listener => listener.remove());
      overlay.current?.setMap(null); overlay.current = null;
      shapes.current.forEach(shape => { google.maps.event.clearInstanceListeners(shape); shape.setMap(null); }); shapes.current = [];
      map.current = null;
      if (callbacks.gm_authFailure === authFailure) callbacks.gm_authFailure = previousAuthFailure;
    };
  }, [key, version, retry]);
  useEffect(() => {
    if (!ready || !map.current) return;
    shapes.current.forEach(shape => { google.maps.event.clearInstanceListeners(shape); shape.setMap(null); });
    const level = props.snapshot.viewport ? mapLevel(props.snapshot.index!, props.snapshot.viewport) : 'country';
    const ids = new Map(props.snapshot.index!.cities.map(city => [city.id, areaIdentity(city, level)[0]]));
    shapes.current = props.snapshot.features.flatMap(feature => polygons(feature.geometry).map(rings => {
      const id = ids.get(feature.id) ?? feature.id;
      const shape = new google.maps.Polygon({ map: map.current,
        paths: rings.map(ring => ring.map(([lng, lat]) => ({ lat, lng }))),
        strokeColor: id === props.selected ? '#D38B00' : '#3F83BE', strokeWeight: level === 'city' ? (id === props.selected ? 2.5 : 1) : 0,
        fillColor: id === props.selected ? '#FFD166' : '#348EF0', fillOpacity: id === props.selected ? 0.33 : 0.12,
      });
      shape.addListener('click', () => latest.current.onSelect(id));
      return shape;
    }));
  }, [ready, props.snapshot.features, props.snapshot.index, props.snapshot.viewport, props.selected]);
  useEffect(() => {
    if (!overlay.current || !ready) return;
    overlay.current.selected = props.selected;
    overlay.current.labels = moving ? [] : placeLabels(props.snapshot, props.selected);
    overlay.current.draw();
  }, [ready, moving, props.snapshot, props.selected]);
  return <View style={styles.fill}>
    <div ref={host} aria-label="Philippine fuel price map by geographic level" style={{ height: '100%', width: '100%' }} />
    {(!key || error || !ready) && <View style={styles.message}>
      <Text style={styles.text}>{!key ? 'The map is not available in this build.' : error ? 'The map could not load. Check your connection and retry.' : 'Loading map…'}</Text>
      {key && error && <Pressable accessibilityRole="button" onPress={() => setRetry(value => value + 1)} style={styles.retry}><Text style={styles.text}>Retry map</Text></Pressable>}
    </View>}
  </View>;
}
const styles = StyleSheet.create({
  fill: { flex: 1 }, message: { position: 'absolute', top: 12, left: 12, right: 12, padding: 14, backgroundColor: '#243142', gap: 8 },
  text: { color: '#EDF2F8', textAlign: 'center' }, retry: { alignSelf: 'center', padding: 10, backgroundColor: '#354B64', borderRadius: 8 },
});
