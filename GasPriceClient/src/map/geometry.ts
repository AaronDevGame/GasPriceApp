import type { Bounds, Feature, Point, PolygonGeometry, Viewport } from './types';

export const DETAIL_ZOOM = 9;
export const MAX_DETAIL_CITIES = 160;
export const MAX_ZOOM = 18;
export const OVERVIEW_PADDING = 20;
export const OVERVIEW_ZOOM_OFFSET = 0.4;
export const LABEL_HEIGHT = 62;
export const FUEL_CARD_PAN_DURATION_MS = 550;
export const intersects = (a: Bounds, b: Bounds) => a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];
const inBox = ([x, y]: Point, b: Bounds) => x >= b[0] && x <= b[2] && y >= b[1] && y <= b[3];
export const polygons = (geometry: PolygonGeometry): Point[][][] => geometry.type === 'Polygon' ? [geometry.coordinates] : geometry.coordinates;
function inRing([x, y]: Point, ring: Point[]) {
  let inside = false;
  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
    const a = ring[i], b = ring[j];
    if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
  }
  return inside;
}
export function insidePolygon(point: Point, rings: Point[][]) {
  return !!rings[0]?.length && inRing(point, rings[0]) && !rings.slice(1).some(hole => inRing(point, hole));
}
// Liang–Barsky segment/rectangle intersection, including boundary touches.
function crossesBox(a: Point, b: Point, box: Bounds) {
  const dx = b[0] - a[0], dy = b[1] - a[1];
  let low = 0, high = 1;
  const ps = [-dx, dx, -dy, dy], qs = [a[0] - box[0], box[2] - a[0], a[1] - box[1], box[3] - a[1]];
  for (let i = 0; i < 4; i++) {
    if (ps[i] === 0) { if (qs[i] < 0) return false; continue; }
    const ratio = qs[i] / ps[i];
    if (ps[i] < 0) low = Math.max(low, ratio); else high = Math.min(high, ratio);
    if (low > high) return false;
  }
  return true;
}
export function featureIntersects(feature: Feature, box: Bounds) {
  const corners: Point[] = [[box[0], box[1]], [box[0], box[3]], [box[2], box[1]], [box[2], box[3]]];
  return polygons(feature.geometry).some(rings => corners.some(p => insidePolygon(p, rings)) ||
    rings.some(ring => ring.some((p, i) => inBox(p, box) || (i > 0 && crossesBox(ring[i - 1], p, box)))));
}
export function containsFeature(feature: Feature, point: Point) {
  return polygons(feature.geometry).some(rings => insidePolygon(point, rings));
}
function ringArea(ring: Point[]) {
  if (ring.length < 3) return 0;
  const [x, y] = ring[0];
  let area = 0;
  for (let i = 2; i < ring.length; i++) {
    const a = ring[i - 1], b = ring[i];
    area += (a[0] - x) * (b[1] - y) - (a[1] - y) * (b[0] - x);
  }
  return Math.abs(area) / 2;
}
// Clip each ring against the four sides of the viewport before measuring its area.
function clipRing(ring: Point[], view: Bounds) {
  for (const side of [0, 1, 2, 3] as const) {
    if (!ring.length) break;
    const axis = side % 2, other = 1 - axis, limit = view[side];
    const inside = (point: Point) => side < 2 ? point[axis] >= limit : point[axis] <= limit;
    const clipped: Point[] = [];
    let previous = ring[ring.length - 1], previousInside = inside(previous);
    for (const point of ring) {
      const pointInside = inside(point);
      if (pointInside !== previousInside) {
        const ratio = (limit - previous[axis]) / (point[axis] - previous[axis]);
        const intersection: Point = [limit, limit];
        intersection[other] = previous[other] + ratio * (point[other] - previous[other]);
        clipped.push(intersection);
      }
      if (pointInside) clipped.push(point);
      previous = point;
      previousInside = pointInside;
    }
    ring = clipped;
  }
  return ring;
}
const boundaryAreas = new WeakMap<PolygonGeometry, number>();
export function visibleAreaFraction(bounds: Bounds, view: Bounds, geometry?: PolygonGeometry) {
  if (!intersects(bounds, view)) return 0;
  if (inBox([bounds[0], bounds[1]], view) && inBox([bounds[2], bounds[3]], view)) return 1;
  if (geometry) {
    const parts = polygons(geometry);
    const area = (clip?: Bounds) => parts.reduce((total, rings) => total + rings.reduce((part, ring, i) =>
      part + (i === 0 ? 1 : -1) * ringArea(clip ? clipRing(ring, clip) : ring), 0), 0);
    let total = boundaryAreas.get(geometry);
    if (total === undefined) {
      total = area();
      boundaryAreas.set(geometry, total);
    }
    if (total > 0) return Math.max(0, Math.min(1, area(view) / total));
  }
  const width = bounds[2] - bounds[0], height = bounds[3] - bounds[1];
  if (width <= 0 || height <= 0) return 1;
  return (Math.min(bounds[2], view[2]) - Math.max(bounds[0], view[0])) / width *
    (Math.min(bounds[3], view[3]) - Math.max(bounds[1], view[1])) / height;
}
export function world([lng, lat]: Point): Point {
  const sin = Math.sin(Math.max(-85, Math.min(85, lat)) * Math.PI / 180);
  return [(lng + 180) / 360, 0.5 - Math.log((1 + sin) / (1 - sin)) / (4 * Math.PI)];
}
export function fitZoom(bounds: Bounds, width: number, height: number, padding = 24) {
  const a = world([bounds[0], bounds[3]]), b = world([bounds[2], bounds[1]]);
  return Math.max(0, Math.min(MAX_ZOOM, Math.log2(Math.min(Math.max(1, width - padding * 2) / (b[0] - a[0]),
    Math.max(1, height - padding * 2) / (b[1] - a[1])) / 256)));
}
// Limit the extra sea on the west while preserving the existing east-side panning room.
export function cameraBounds(bounds: Bounds): Bounds {
  const longitudeSpan = bounds[2] - bounds[0];
  const latitudePadding = (bounds[3] - bounds[1]) * 0.1;
  return [bounds[0] - longitudeSpan * 0.05, bounds[1] - latitudePadding,
    bounds[2] + longitudeSpan * 0.25, bounds[3] + latitudePadding];
}
// Center area bounds in the map's Mercator projection, without an east/west offset.
export function boundsCenter(bounds: Bounds): Point {
  const north = world([bounds[0], bounds[3]])[1], south = world([bounds[2], bounds[1]])[1];
  return [(bounds[0] + bounds[2]) / 2, Math.atan(Math.sinh(Math.PI * (1 - north - south))) * 180 / Math.PI];
}
// The nationwide overview frames east of its bounds to balance offshore islands.
export function cameraCenter(bounds: Bounds): Point {
  const [longitude, latitude] = boundsCenter(bounds);
  return [longitude + (bounds[2] - bounds[0]) * 0.1, latitude];
}
export const midpoint = (b: Bounds): Point => [(b[0] + b[2]) / 2, (b[1] + b[3]) / 2];
export function project(point: Point, viewport: Viewport): Point {
  const p = world(point), nw = world([viewport.bounds[0], viewport.bounds[3]]), se = world([viewport.bounds[2], viewport.bounds[1]]);
  return [(p[0] - nw[0]) / (se[0] - nw[0]) * viewport.width, (p[1] - nw[1]) / (se[1] - nw[1]) * viewport.height];
}
export function pointAtPixel([x, y]: Point, v: Viewport): Point {
  const nw = world([v.bounds[0], v.bounds[3]]), se = world([v.bounds[2], v.bounds[1]]);
  const wx = nw[0] + x / v.width * (se[0] - nw[0]), wy = nw[1] + y / v.height * (se[1] - nw[1]);
  return [wx * 360 - 180, Math.atan(Math.sinh(Math.PI * (1 - 2 * wy))) * 180 / Math.PI];
}
export function rectangleInside(feature: Feature, box: Bounds) {
  const corners: Point[] = [[box[0], box[1]], [box[0], box[3]], [box[2], box[1]], [box[2], box[3]]];
  return polygons(feature.geometry).some(rings => corners.every(p => insidePolygon(p, rings)) &&
    !rings.some(ring => ring.some((p, i) => inBox(p, box) || (i > 0 && crossesBox(ring[i - 1], p, box)))));
}
