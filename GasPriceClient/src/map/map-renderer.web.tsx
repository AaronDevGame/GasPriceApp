import { Asset } from 'expo-asset';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';

import { fuelNozzles } from '@/fuel/nozzles';

import { boundsCenter, cameraBounds, fitZoom, FUEL_CARD_PAN_DURATION_MS, MAX_ZOOM, polygons } from './geometry';
import { areaIdentity, defaultMapCamera, mapLevel } from './hierarchy';
import { loadGoogleMaps } from './google-loader.web';
import { edgeMarker, edgeMarkerStatus, type LabelRectangle, type MapLabel, mapLabelColors, mapLabelNozzle, mapLabels, webLabelPosition } from './labels';
import type { Bounds, MapRendererProps, Point, Viewport } from './types';

const literalBounds = (bounds: Bounds) => ({ west: bounds[0], south: bounds[1], east: bounds[2], north: bounds[3] });
const geographicBounds = (bounds: google.maps.LatLngBounds): Bounds => {
  const { west, south, east, north } = bounds.toJSON();
  return [west, south, east, north];
};
const nozzleUris = {
  gasoline: Asset.fromModule(fuelNozzles.gasoline).uri,
  diesel: Asset.fromModule(fuelNozzles.diesel).uri,
};
type LabelOverlay = google.maps.OverlayView & {
  setLabels(labels: MapLabel[], selected: string | null): void;
  scheduleDraw(): void;
};
type AreaPolygon = { shape: google.maps.Polygon; areaId: string; hasOutline: boolean };
type LabelCard = {
  label: MapLabel;
  positioner: HTMLDivElement;
  button: HTMLButtonElement;
  name: HTMLSpanElement;
  tail: HTMLSpanElement;
  regular: HTMLSpanElement;
  compact: HTMLSpanElement;
  edge: boolean;
  appearance: string;
  priority: number;
  offset?: Point;
};
function createLabels(map: google.maps.Map, onSelect: (label: MapLabel) => void) {
      class Labels extends google.maps.OverlayView {
        private root = document.createElement('div');
        private cards = new Map<string, LabelCard>();
        private frame: number | undefined;
        private zoom: number | undefined;
        private selectedId: string | null = null;
        onAdd() { this.root.style.cssText = 'position:absolute;left:0;top:0;pointer-events:none;'; this.getPanes()?.floatPane.appendChild(this.root); }
        setLabels(labels: MapLabel[], selectedId: string | null) {
          const nextCards = new Map<string, LabelCard>();
          labels.forEach((label, priority) => {
            const selected = label.id === selectedId;
            const appearance = JSON.stringify([selected, label.center, label.accessibilityLabel, label.name,
              label.priceRows, label.skeletonWidths, label.coverage, label.width, label.height, label.fontSize]);
            const previous = this.cards.get(label.id);
            const positioner = previous?.positioner ?? document.createElement('div');
            if (!previous) {
              positioner.style.cssText = 'position:absolute;left:0;top:0;visibility:hidden;';
              this.root.appendChild(positioner);
            }
            const keepOffset = previous && this.selectedId === selectedId &&
              previous.label.width === label.width && previous.label.height === label.height;
            const offset = keepOffset ? previous.offset : undefined;
            if (previous?.appearance === appearance) {
              nextCards.set(label.id, { ...previous, label, priority, offset });
              return;
            }
            const card = document.createElement('button');
            card.type = 'button';
            google.maps.OverlayView.preventMapHitsFrom(card);
            card.addEventListener('click', () => onSelect(label));
            const hasPrices = label.priceRows.length > 0;
            const pending = label.skeletonWidths.length > 0;
            const colors = selected ? mapLabelColors.selected : pending ? mapLabelColors.skeleton : mapLabelColors.neutral;
            card.setAttribute('aria-label', label.accessibilityLabel);
            if (selected) card.setAttribute('aria-current', 'true');
            card.style.cssText = `appearance:none;box-sizing:border-box;padding:4px;overflow:hidden;position:absolute;left:0;top:0;transform:translate(-50%,-50%);width:${label.width}px;height:${label.height}px;color:${selected || hasPrices || pending ? 'white' : mapLabelColors.unavailableText};border:2px solid ${colors.border};border-radius:7px;font:600 ${label.fontSize}px/16px system-ui;white-space:pre;text-align:center;pointer-events:auto;cursor:pointer;user-select:none;font-variant-numeric:tabular-nums;display:flex;flex-direction:column;align-items:center;justify-content:center;`;
            card.style.backgroundColor = colors.background;
            const name = document.createElement('span');
            name.textContent = label.name;
            name.style.cssText = 'max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
            card.append(name);
            const regular = document.createElement('span');
            const compact = document.createElement('span');
            regular.style.display = 'contents';
            compact.style.display = 'none';
            card.append(regular, compact);
            if (pending) {
              card.setAttribute('aria-busy', 'true');
              card.style.gap = '9px';
              for (const width of label.skeletonWidths) {
                const bar = document.createElement('span');
                bar.setAttribute('aria-hidden', 'true');
                bar.style.cssText = `flex:none;width:${width}%;height:7px;border-radius:4px;background:${mapLabelColors.skeleton.bar};`;
                card.append(bar);
              }
            } else {
              if (hasPrices) compact.style.width = `${2 * edgeMarker.columnWidth}px`;
              for (const { fuel, text, edgeText } of label.priceRows) {
                const row = document.createElement('span');
                row.style.cssText = `display:flex;align-items:center;gap:${mapLabelNozzle.gap}px;`;
                const nozzle = document.createElement('img');
                nozzle.src = nozzleUris[fuel];
                nozzle.alt = '';
                nozzle.width = mapLabelNozzle.size;
                nozzle.height = mapLabelNozzle.size;
                nozzle.style.objectFit = 'contain';
                row.append(nozzle, text);
                regular.append(row);
                const column = document.createElement('span');
                column.style.cssText = `display:flex;flex:none;flex-direction:column;align-items:center;width:${edgeMarker.columnWidth}px;`;
                if (fuel === 'diesel') column.style.marginLeft = 'auto';
                const compactNozzle = nozzle.cloneNode() as HTMLImageElement;
                compactNozzle.width = edgeMarker.nozzleSize;
                compactNozzle.height = edgeMarker.nozzleSize;
                column.append(compactNozzle, edgeText);
                compact.append(column);
              }
              if (label.coverage) {
                const status = document.createElement(hasPrices ? 'span' : 'em');
                status.textContent = label.coverage;
                regular.append(status);
                if (!hasPrices) {
                  const shortStatus = document.createElement('em');
                  shortStatus.textContent = edgeMarkerStatus;
                  compact.append(shortStatus);
                }
              }
            }
            const { diameter, tailLength, tailOverlap, tailHalfWidth } = edgeMarker;
            const tail = document.createElement('span');
            tail.style.cssText = `display:none;position:absolute;left:0;top:0;width:0;height:0;pointer-events:none;`;
            const triangle = document.createElement('span');
            triangle.style.cssText = `position:absolute;left:${diameter / 2 - tailOverlap}px;top:${-tailHalfWidth}px;border-top:${tailHalfWidth}px solid transparent;border-bottom:${tailHalfWidth}px solid transparent;border-left:${tailLength}px solid ${colors.border};`;
            tail.append(triangle);
            positioner.replaceChildren(tail, card);
            nextCards.set(label.id, { label, positioner, button: card, name, tail, regular, compact, edge: false, appearance, priority, offset });
          });
          for (const [id, card] of this.cards) {
            if (!nextCards.has(id)) card.positioner.remove();
          }
          this.cards = nextCards;
          this.selectedId = selectedId;
          this.draw();
        }
        scheduleDraw() {
          this.frame ??= requestAnimationFrame(() => {
            this.frame = undefined;
            this.draw();
          });
        }
        draw() {
          if (this.frame !== undefined) {
            cancelAnimationFrame(this.frame);
            this.frame = undefined;
          }
          const projection = this.getProjection();
          if (!projection) return;
          const center = map.getCenter();
          const paneCenter = center && projection.fromLatLngToDivPixel(center);
          const containerCenter = center && projection.fromLatLngToContainerPixel(center);
          const visible = map.getBounds();
          if (!paneCenter || !containerCenter || !visible) return;
          const host = map.getDiv();
          const viewport = { bounds: geographicBounds(visible), width: host.clientWidth, height: host.clientHeight };
          const left = paneCenter.x - containerCenter.x;
          const top = paneCenter.y - containerCenter.y;
          const projectPoint = ([lng, lat]: Point): Point | null => {
            const point = projection.fromLatLngToContainerPixel({ lng, lat });
            return point && [point.x, point.y];
          };
          const placed: LabelRectangle[] = [];
          const zoom = map.getZoom();
          for (const card of this.cards.values()) {
            const { label, positioner, priority, offset } = card;
            const position = webLabelPosition(label, viewport, projectPoint, placed, priority, this.cards.size,
              zoom === this.zoom ? offset : undefined);
            card.offset = position?.offset;
            positioner.style.visibility = position ? 'visible' : 'hidden';
            if (position) {
              if (!position.edgePushed) placed.push(position);
              positioner.style.zIndex = String(position.zIndex);
              positioner.style.transform = `translate(${position.x + left}px, ${position.y + top}px)`;
              const edge = position.tailAngle !== undefined;
              const { button, name, tail, regular, compact } = card;
              if (edge) tail.style.transform = `rotate(${position.tailAngle}rad)`;
              if (card.edge === edge) continue;
              card.edge = edge;
              button.style.width = `${position.width}px`;
              button.style.height = `${position.height}px`;
              button.style.borderRadius = edge ? '50%' : '7px';
              button.style.padding = edge ? `${edgeMarker.padding}px` : '4px';
              button.style.fontSize = `${edge ? edgeMarker.fontSize : label.fontSize}px`;
              button.style.lineHeight = edge ? `${edgeMarker.lineHeight}px` : '16px';
              name.style.fontSize = edge ? `${edgeMarker.nameFontSize}px` : '';
              if (label.skeletonWidths.length) button.style.gap = edge ? '4px' : '9px';
              regular.style.display = edge ? 'none' : 'contents';
              compact.style.display = edge ? 'flex' : 'none';
              tail.style.display = edge ? 'block' : 'none';
            }
          }
          this.zoom = zoom;
        }
        onRemove() {
          if (this.frame !== undefined) cancelAnimationFrame(this.frame);
          this.frame = undefined;
          this.root.remove();
        }
      }
  return new Labels();
}
export default function MapRenderer(props: MapRendererProps) {
  const host = useRef<HTMLDivElement | null>(null);
  const latest = useRef(props);
  const map = useRef<google.maps.Map | null>(null);
  const cardPanFrame = useRef<number | null>(null);
  const cancelCardPan = useCallback(() => {
    if (cardPanFrame.current !== null) cancelAnimationFrame(cardPanFrame.current);
    cardPanFrame.current = null;
  }, []);
  const focusedBounds = useRef<Bounds | null>(null);
  const focusedZoom = useRef<MapRendererProps['zoomFocus']>(null);
  const focusedLocation = useRef<MapRendererProps['locationFocus']>(null);
  const shapes = useRef<AreaPolygon[]>([]);
  const overlay = useRef<LabelOverlay | null>(null);
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
      const labels = createLabels(instance, label => {
        cancelCardPan();
        const [lng, lat] = label.center;
        const origin = instance.getCenter()?.toJSON();
        if (!origin) {
          instance.setCenter({ lng, lat });
        } else if (origin.lng !== lng || origin.lat !== lat) {
          const start = performance.now();
          const pan = (now: number) => {
            const progress = Math.min(1, (now - start) / FUEL_CARD_PAN_DURATION_MS);
            if (progress === 1) {
              cardPanFrame.current = null;
              instance.setCenter({ lng, lat });
              return;
            }
            const eased = 1 - (1 - progress) ** 3;
            instance.setCenter({ lng: origin.lng + (lng - origin.lng) * eased, lat: origin.lat + (lat - origin.lat) * eased });
            cardPanFrame.current = requestAnimationFrame(pan);
          };
          cardPanFrame.current = requestAnimationFrame(pan);
        }
        latest.current.onSelect(label.id);
      });
      labels.setMap(instance); overlay.current = labels;
      const readViewport = (): Viewport | undefined => {
        const visible = instance.getBounds();
        if (!visible || !host.current?.clientWidth || !host.current.clientHeight || !active) return;
        return { bounds: geographicBounds(visible), zoom: instance.getZoom() ?? 12,
          width: host.current.clientWidth, height: host.current.clientHeight };
      };
      listeners.push(instance.addListener('bounds_changed', () => {
        labels.scheduleDraw();
        latest.current.onMove(readViewport());
      }));
      listeners.push(instance.addListener('idle', () => {
        if (cardPanFrame.current !== null) return;
        const viewport = readViewport();
        if (viewport) latest.current.onIdle(viewport);
      }));
      listeners.push(instance.addListener('dragstart', cancelCardPan));
      listeners.push(instance.addListener('zoom_changed', cancelCardPan));
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
      cancelCardPan();
      active = false; resize?.disconnect(); listeners.forEach(listener => listener.remove());
      overlay.current?.setMap(null); overlay.current = null;
      map.current = null;
      if (callbacks.gm_authFailure === authFailure) callbacks.gm_authFailure = previousAuthFailure;
    };
  }, [key, version, retry, cancelCardPan]);
  useEffect(() => {
    if (!ready || !map.current || props.active === false || !props.focusBounds || !host.current?.clientWidth || focusedBounds.current === props.focusBounds) return;
    cancelCardPan();
    map.current.fitBounds(literalBounds(props.focusBounds), 36);
    focusedBounds.current = props.focusBounds;
  }, [ready, props.focusBounds, props.active, cancelCardPan]);
  useEffect(() => {
    const point = props.locationFocus;
    if (!ready || !map.current || props.active === false || !point || !host.current?.clientWidth || focusedLocation.current === point) return;
    cancelCardPan();
    const [lng, lat] = point.areaBounds ? boundsCenter(point.areaBounds) : [point.longitude, point.latitude];
    map.current.setCenter({ lat, lng });
    map.current.setZoom(12);
    focusedLocation.current = point;
  }, [ready, props.locationFocus, props.active, cancelCardPan]);
  useEffect(() => {
    const focus = props.zoomFocus;
    if (!ready || !map.current || props.active === false || !focus || !host.current?.clientWidth || focusedZoom.current === focus) return;
    cancelCardPan();
    if (focus.center) map.current.setCenter({ lng: focus.center[0], lat: focus.center[1] });
    map.current.setZoom(focus.zoom);
    focusedZoom.current = focus;
  }, [ready, props.zoomFocus, props.active, cancelCardPan]);
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
    overlay.current.setLabels(mapLabels(props.snapshot, props.selected), props.selected);
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
