import type { Bounds, Feature, Point, PolygonGeometry, Viewport } from './types';

export const DETAIL_ZOOM = 9;
export const MAX_DETAIL_CITIES = 160;
export const MAX_ZOOM = 18;
export const LABEL_HEIGHT = 62;
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
export function world([lng, lat]: Point): Point {
  const sin = Math.sin(Math.max(-85, Math.min(85, lat)) * Math.PI / 180);
  return [(lng + 180) / 360, 0.5 - Math.log((1 + sin) / (1 - sin)) / (4 * Math.PI)];
}
export function fitZoom(bounds: Bounds, width: number, height: number, padding = 24) {
  const a = world([bounds[0], bounds[3]]), b = world([bounds[2], bounds[1]]);
  return Math.max(0, Math.min(MAX_ZOOM, Math.log2(Math.min(Math.max(1, width - padding * 2) / (b[0] - a[0]),
    Math.max(1, height - padding * 2) / (b[1] - a[1])) / 256)));
}
export const cameraBounds = (bounds: Bounds): Bounds => [bounds[0] - 0.25, bounds[1] - 0.25, bounds[2] + 0.25, bounds[3] + 0.25];
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
