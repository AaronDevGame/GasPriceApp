import { LABEL_HEIGHT, pointAtPixel, project, rectangleInside } from './geometry';
import type { City, CityPrices, Feature, MapSnapshot, Point, PriceRange, Viewport } from './types';

export const rangeText = (value: PriceRange | null) => value
  ? `₱${value.minPrice.toFixed(2)}–₱${value.maxPrice.toFixed(2)}/L` : '—';
export type MapLabel = { id: string; city: City; price: CityPrices; anchor: Point; x: number; y: number; width: number };
// Search the part of the city's bounding box where a full label can fit on screen.
function* candidates(city: City, viewport: Viewport, width: number): Generator<Point> {
  const nw = project([city.bbox[0], city.bbox[3]], viewport);
  const se = project([city.bbox[2], city.bbox[1]], viewport);
  const left = Math.max(8, nw[0]) + width / 2, right = Math.min(viewport.width - 8, se[0]) - width / 2;
  const top = Math.max(8, nw[1]) + LABEL_HEIGHT / 2, bottom = Math.min(viewport.height - 36, se[1]) - LABEL_HEIGHT / 2;
  if (left > right || top > bottom) return;
  const [x, y] = project(city.labelPoint, viewport);
  const preferred: Point = [Math.max(left, Math.min(x, right)), Math.max(top, Math.min(y, bottom))];
  yield preferred;

  // Bounded fallback for clipped cities and narrow or concave polygons.
  const grid: Point[] = [];
  for (let row = 0; row < 5; row++) {
    for (let column = 0; column < 5; column++) {
      grid.push([left + (column + 0.5) * (right - left) / 5, top + (row + 0.5) * (bottom - top) / 5]);
    }
  }
  const distance = ([px, py]: Point) => (px - preferred[0]) ** 2 + (py - preferred[1]) ** 2;
  yield* grid.sort((a, b) => distance(a) - distance(b));
}
export function placeLabels(snapshot: MapSnapshot, selected: string | null): MapLabel[] {
  const viewport = snapshot.viewport;
  if (!viewport || !snapshot.detail) return [];
  const features = new Map<string, Feature>(snapshot.features.map(feature => [feature.id, feature]));
  const placed: MapLabel[] = [];
  const hasRange = (city: City) => {
    const price = snapshot.prices.get(city.id);
    return Number(!!(price?.gasoline || price?.diesel));
  };
  const sorted = [...snapshot.cities].sort((a, b) =>
    Number(b.id === selected) - Number(a.id === selected) || hasRange(b) - hasRange(a) || a.id.localeCompare(b.id));
  for (const city of sorted) {
    const price = snapshot.prices.get(city.id), feature = features.get(city.id);
    if (!price || !feature || Date.parse(price.expiresAtUtc) <= Date.now()) continue;
    const longestLine = Math.max(rangeText(price.gasoline).length, rangeText(price.diesel).length);
    const width = Math.max(44, longestLine * 7 + 18);
    for (const [x, y] of candidates(city, viewport, width)) {
      if (placed.some(other => Math.abs(other.x - x) < (other.width + width) / 2 + 8 && Math.abs(other.y - y) < LABEL_HEIGHT + 8)) continue;
      const sw = pointAtPixel([x - width / 2, y + LABEL_HEIGHT / 2], viewport);
      const ne = pointAtPixel([x + width / 2, y - LABEL_HEIGHT / 2], viewport);
      if (!rectangleInside(feature, [sw[0], sw[1], ne[0], ne[1]])) continue;
      placed.push({ id: city.id, city, price, anchor: pointAtPixel([x, y], viewport), x, y, width });
      break;
    }
  }
  return placed;
}
