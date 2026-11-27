import { useEffect, useMemo, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { boundsCenter, cameraBounds, fitZoom, MAX_ZOOM, polygons, project } from './geometry';
import { areaIdentity, defaultMapCamera, mapLevel } from './hierarchy';
import { loadGoogleMaps } from './google-loader.web';
import { type MapLabel, mapLabelColors, placeLabels } from './labels';
import type { Bounds, MapRendererProps, Point, Viewport } from './types';

const literalBounds = (bounds: Bounds) => ({ west: bounds[0], south: bounds[1], east: bounds[2], north: bounds[3] });
type LabelOverlay = google.maps.OverlayView & { setLabels(labels: MapLabel[], selected: string | null): void };
type AreaPolygon = { shape: google.maps.Polygon; areaId: string; hasOutline: boolean };
type LabelCard = {
  anchor: google.maps.LatLng;
  positioner: HTMLDivElement;
  appearance: string;
  glide?: Animation;
};
function createLabels(onSelect: (label: MapLabel) => void) {
      class Labels extends google.maps.OverlayView {
        private root = document.createElement('div');
        private cards = new Map<string, LabelCard>();
        onAdd() { this.root.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;'; this.getPanes()?.floatPane.appendChild(this.root); }
        setLabels(labels: MapLabel[], selectedId: string | null) {
          const nextCards = new Map<string, LabelCard>();
          const projection = this.getProjection();
          const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
          labels.forEach((label, priority) => {
            const selected = label.id === selectedId;
            const dashed = label.price.estimated || label.price.outdated;
            const appearance = JSON.stringify([selected, dashed, label.center, label.accessibilityLabel, label.name,
              label.priceLines, label.skeletonWidths, label.coverage, label.width, label.height, label.fontSize]);
            const anchor = new google.maps.LatLng(label.anchor[1], label.anchor[0]);
            const previous = this.cards.get(label.id);
            const positioner = previous?.positioner ?? document.createElement('div');
            if (!previous) {
              positioner.style.cssText = 'position:absolute;left:0;top:0;visibility:hidden;';
              this.root.appendChild(positioner);
            }
            positioner.style.zIndex = String(labels.length - priority);
            let glide = previous?.glide;
            if (reduceMotion) glide?.cancel();
            if (previous && !reduceMotion && !previous.anchor.equals(anchor)) {
              const from = projection?.fromLatLngToDivPixel(previous.anchor);
              const to = projection?.fromLatLngToDivPixel(anchor);
              if (from && to) {
                // Add only the layout displacement. The camera transform still updates immediately.
                const [x, y] = getComputedStyle(positioner).translate.split(' ').map(parseFloat);
                glide?.cancel();
                glide = positioner.animate([
                  { translate: `${from.x - to.x + (x || 0)}px ${from.y - to.y + (y || 0)}px` },
                  { translate: '0px 0px' },
                ], { duration: 150, easing: 'ease-out' });
              }
            }
            nextCards.set(label.id, { anchor, positioner, appearance, glide });
            if (previous?.appearance === appearance) return;
            const card = document.createElement('button');
            card.type = 'button';
            google.maps.OverlayView.preventMapHitsFrom(card);
            card.addEventListener('click', () => onSelect(label));
            const hasPrices = label.priceLines.length > 0;
            const pending = label.skeletonWidths.length > 0;
            const colors = selected && (hasPrices || pending) ? mapLabelColors.selected : pending ? mapLabelColors.skeleton :
              hasPrices ? mapLabelColors.unselected : mapLabelColors.unavailable;
            const border = selected ? mapLabelColors.selected.border : colors.border;
            card.setAttribute('aria-label', label.accessibilityLabel);
            card.style.cssText = `appearance:none;box-sizing:border-box;padding:4px;overflow:hidden;position:absolute;left:0;top:0;transform:translate(-50%,-50%);width:${label.width}px;height:${label.height}px;color:${hasPrices || pending ? 'white' : mapLabelColors.unavailable.text};border:2px solid ${border};border-radius:7px;font:600 ${label.fontSize}px/16px system-ui;white-space:pre;text-align:center;pointer-events:auto;cursor:pointer;user-select:none;font-variant-numeric:tabular-nums;`;
            card.style.backgroundColor = colors.background;
            if (hasPrices && !selected) {
              card.style.backgroundImage = mapLabelColors.unselected.gradient;
              card.style.textShadow = `0 1px 2px ${mapLabelColors.textShadow}, 0 0 4px ${mapLabelColors.textShadow}`;
            }
            if (pending) {
              card.setAttribute('aria-busy', 'true');
              card.style.cssText += 'display:flex;flex-direction:column;align-items:center;justify-content:center;gap:9px;';
              const name = document.createElement('span');
              name.textContent = label.name;
              name.style.cssText = 'max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
              card.append(name);
              for (const width of label.skeletonWidths) {
                const bar = document.createElement('span');
                bar.setAttribute('aria-hidden', 'true');
                bar.style.cssText = `flex:none;width:${width}%;height:7px;border-radius:4px;background:${mapLabelColors.skeleton.bar};`;
                card.append(bar);
              }
            } else {
              card.textContent = [label.name, ...label.priceLines].join('\n');
              const status = document.createElement(hasPrices ? 'span' : 'em');
              status.textContent = label.coverage;
              card.append('\n', status);
            }
            if (dashed) card.style.borderStyle = 'dashed';
            positioner.replaceChildren(card);
          });
          for (const [id, card] of this.cards) {
            if (!nextCards.has(id)) {
              card.glide?.cancel();
              card.positioner.remove();
            }
          }
          this.cards = nextCards;
          this.draw();
        }
        draw() {
          const projection = this.getProjection();
          if (!projection) return;
          // Google Maps calls draw as the camera moves. Reuse the cards and update only their positions.
          for (const { anchor, positioner } of this.cards.values()) {
            const point = projection.fromLatLngToDivPixel(anchor);
            positioner.style.visibility = point ? 'visible' : 'hidden';
            if (point) {
              positioner.style.transform = `translate(${point.x}px, ${point.y}px)`;
            }
          }
        }
        onRemove() {
          for (const { glide } of this.cards.values()) glide?.cancel();
          this.root.remove();
        }
      }
  return new Labels();
}
export default function MapRenderer(props: MapRendererProps) {
  const host = useRef<HTMLDivElement | null>(null);
  const latest = useRef(props);
  const map = useRef<google.maps.Map | null>(null);
  const focusedBounds = useRef<Bounds | null>(null);
  const focusedZoom = useRef<MapRendererProps['zoomFocus']>(null);
  const focusedLocation = useRef<MapRendererProps['locationFocus']>(null);
  const shapes = useRef<AreaPolygon[]>([]);
  const overlay = useRef<LabelOverlay | null>(null);
  const isPanning = useRef(false);
  const labelAnchors = useRef(new Map<string, Point>());
  const labelViewport = useRef<Viewport | null>(null);
  const [ready, setReady] = useState(false);
  const [error, setError] = useState(false);
  const [retry, setRetry] = useState(0);
  const key = process.env.EXPO_PUBLIC_GOOGLE_MAPS_WEB_API_KEY;
  const version = props.snapshot.index!.version;
  const index = props.snapshot.index!;
  const level = props.snapshot.viewport ? mapLevel(index, props.snapshot.viewport) : 'country';
  const areaIds = useMemo(() => new Map(index.cities.map(city => [city.id, areaIdentity(city, level)[0]])), [index, level]);
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
      let cameraInitialized = !!(host.current.clientWidth && host.current.clientHeight);
      const { center: [lng, lat], zoom } = defaultMapCamera(index, {
        width: host.current.clientWidth, height: host.current.clientHeight,
      }, latest.current.metroBounds);

      const instance = new google.maps.Map(host.current, {
        center: { lat, lng }, zoom, maxZoom: MAX_ZOOM, isFractionalZoomEnabled: true,
        restriction: { latLngBounds: literalBounds(bounds), strictBounds: false },
        mapTypeControl: false, streetViewControl: false, fullscreenControl: false,
        rotateControl: false, tilt: 0, heading: 0, gestureHandling: 'greedy',
        styles: [{ featureType: 'poi', stylers: [{ visibility: 'off' }] }, { featureType: 'transit', stylers: [{ visibility: 'off' }] }],
      });
      map.current = instance; focusedBounds.current = null; focusedLocation.current = null; focusedZoom.current = null;
      const labels = createLabels(label => {
        const [lng, lat] = label.center;
        instance.setCenter({ lng, lat });
        latest.current.onSelect(label.id);
      });
      labels.setMap(instance); overlay.current = labels;
      const readViewport = (): Viewport | undefined => {
        const visible = instance.getBounds();
        if (!visible || !host.current?.clientWidth || !host.current.clientHeight || !active) return;
        const sw = visible.getSouthWest(), ne = visible.getNorthEast();
        return { bounds: [sw.lng(), sw.lat(), ne.lng(), ne.lat()], zoom: instance.getZoom() ?? 12,
          width: host.current.clientWidth, height: host.current.clientHeight };
      };
      listeners.push(instance.addListener('bounds_changed', () => latest.current.onMove(readViewport())));
      listeners.push(instance.addListener('dragstart', () => { isPanning.current = true; }));
      listeners.push(instance.addListener('dragend', () => { isPanning.current = false; }));
      listeners.push(instance.addListener('idle', () => {
        isPanning.current = false;
        const viewport = readViewport();
        if (viewport) latest.current.onIdle(viewport);
      }));
      const updateSize = () => {
        const width = host.current?.clientWidth, height = host.current?.clientHeight;
        if (!width || !height) return;
        instance.setOptions({ minZoom: fitZoom(bounds, width, height, 0) });
        if (!cameraInitialized) {
          const { center: [lng, lat], zoom } = defaultMapCamera(index, { width, height }, latest.current.metroBounds);
          instance.setCenter({ lat, lng });
          instance.setZoom(zoom);
          cameraInitialized = true;
        }
        setReady(true);
      };
      resize = new ResizeObserver(updateSize); resize.observe(host.current); updateSize();
    }).catch(() => { if (active) setError(true); });
    return () => {
      active = false; resize?.disconnect(); listeners.forEach(listener => listener.remove());
      overlay.current?.setMap(null); overlay.current = null;
      isPanning.current = false;
      labelAnchors.current.clear();
      labelViewport.current = null;
      map.current = null;
      if (callbacks.gm_authFailure === authFailure) callbacks.gm_authFailure = previousAuthFailure;
    };
  }, [key, version, retry]);
  useEffect(() => {
    if (!ready || !map.current || props.active === false || !props.focusBounds || !host.current?.clientWidth || focusedBounds.current === props.focusBounds) return;
    map.current.fitBounds(literalBounds(props.focusBounds), 36);
    focusedBounds.current = props.focusBounds;
  }, [ready, props.focusBounds, props.active]);
  useEffect(() => {
    const point = props.locationFocus;
    if (!ready || !map.current || props.active === false || !point || !host.current?.clientWidth || focusedLocation.current === point) return;
    const [lng, lat] = point.areaBounds ? boundsCenter(point.areaBounds) : [point.longitude, point.latitude];
    map.current.setCenter({ lat, lng });
    map.current.setZoom(12);
    focusedLocation.current = point;
  }, [ready, props.locationFocus, props.active]);
  useEffect(() => {
    const focus = props.zoomFocus;
    if (!ready || !map.current || props.active === false || !focus || !host.current?.clientWidth || focusedZoom.current === focus) return;
    if (focus.center) map.current.setCenter({ lng: focus.center[0], lat: focus.center[1] });
    map.current.setZoom(focus.zoom);
    focusedZoom.current = focus;
  }, [ready, props.zoomFocus, props.active]);
  useEffect(() => {
    if (!ready || !map.current) return;
    shapes.current = props.snapshot.features.map(feature => {
      const areaId = areaIds.get(feature.id) ?? feature.id;
      // A Maps polygon accepts disjoint outer rings and oppositely wound holes.
      const shape = new google.maps.Polygon({
        map: map.current, clickable: true,
        paths: polygons(feature.geometry).flatMap(rings => rings.map(ring => ring.map(([lng, lat]) => ({ lat, lng })))),
      });
      shape.addListener('click', () => latest.current.onSelect(areaId));
      return { shape, areaId, hasOutline: feature.id === areaId };
    });
    return () => {
      for (const { shape } of shapes.current) {
        google.maps.event.clearInstanceListeners(shape);
        shape.setMap(null);
      }
      shapes.current = [];
    };
  }, [ready, props.snapshot.features, areaIds]);
  useEffect(() => {
    for (const { shape, areaId, hasOutline } of shapes.current) {
      const selected = areaId === props.selected;
      shape.setOptions({
        strokeColor: selected ? '#D38B00' : '#3F83BE', strokeWeight: hasOutline ? (selected ? 2.5 : 1) : 0,
        fillColor: selected ? '#FFD166' : '#348EF0', fillOpacity: selected ? 0.33 : 0.12,
        zIndex: selected ? 1 : 0,
      });
    }
  }, [ready, props.snapshot.features, areaIds, props.selected]);
  useEffect(() => {
    if (!overlay.current || !ready) return;
    const viewport = props.snapshot.viewport;
    const previous = labelViewport.current;
    const sameScale = viewport && previous && viewport.zoom === previous.zoom &&
      viewport.width === previous.width && viewport.height === previous.height;
    const dragOffset = sameScale && isPanning.current && viewport !== previous
      ? project([previous.bounds[0], previous.bounds[3]], viewport) : undefined;
    const labels = placeLabels(props.snapshot, props.selected, sameScale ? labelAnchors.current : undefined, dragOffset);
    labelAnchors.current = new Map(labels.map(label => [label.id, label.anchor]));
    labelViewport.current = viewport;
    overlay.current.setLabels(labels, props.selected);
  }, [ready, props.snapshot, props.selected]);
  return <View style={styles.fill}>
    <div ref={host} data-pull-refresh-ignore aria-label="Philippine fuel price map by geographic level" style={{ height: '100%', width: '100%' }} />
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
