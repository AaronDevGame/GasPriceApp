import { LABEL_HEIGHT, pointAtPixel, project } from './geometry';
import type { City, CityPrices, MapSnapshot, Point, PriceRange, Viewport } from './types';

export const rangeText = (value: PriceRange | null) => value
  ? `₱${value.minPrice.toFixed(2)}–₱${value.maxPrice.toFixed(2)}/L` : '—';
export type MapLabel = { id: string; city: City; price: CityPrices; anchor: Point; x: number; y: number; width: number; height: number; fontSize: number; name: string };
// Search the part of the city's bounding box where a full label can fit on screen.
function* candidates(city: City, viewport: Viewport, width: number, height: number): Generator<Point> {
  const nw = project([city.bbox[0], city.bbox[3]], viewport);
  const se = project([city.bbox[2], city.bbox[1]], viewport);
  const left = Math.max(8 + width / 2, nw[0]), right = Math.min(viewport.width - 8 - width / 2, se[0]);
  const top = Math.max(8 + height / 2, nw[1]), bottom = Math.min(viewport.height - 36 - height / 2, se[1]);
  if (viewport.width < width + 16 || viewport.height < height + 44) return;
  if (left > right || top > bottom) {
    yield [Math.max(8 + width / 2, Math.min(viewport.width - 8 - width / 2, (nw[0] + se[0]) / 2)),
      Math.max(8 + height / 2, Math.min(viewport.height - 36 - height / 2, (nw[1] + se[1]) / 2))];
    return;
  }
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
  if (!viewport) return [];
  const placed: MapLabel[] = [];
  const hasRange = (city: City) => {
    const price = snapshot.prices.get(city.id);
    return Number(!!(price?.gasoline || price?.diesel));
  };
  const sorted = [...snapshot.cities].sort((a, b) =>
    Number(b.id === selected) - Number(a.id === selected) || hasRange(b) - hasRange(a) || a.id.localeCompare(b.id));
  for (const city of sorted) {
    const price = snapshot.prices.get(city.id);
    if (!price || Date.parse(price.expiresAtUtc) <= Date.now()) continue;
    const name = city.name?.trim() || city.groupName;
    const fontSize = viewport.zoom < 10 ? 10 : 11;
    const height = viewport.zoom < 10 ? 54 : LABEL_HEIGHT;
    const longestLine = Math.max(name.length, rangeText(price.gasoline).length, rangeText(price.diesel).length);
    const width = Math.min(viewport.width - 16, Math.max(90, longestLine * fontSize * 0.64 + 18));
    const positions = [...candidates(city, viewport, width, height)];
    const position = positions.find(([x, y]) => !placed.some(other =>
      Math.abs(other.x - x) < (other.width + width) / 2 + 8 && Math.abs(other.y - y) < (other.height + height) / 2 + 8)) ?? positions[0];
    // Dense views may overlap, but zoom and small boundaries never suppress a city price.
    if (position) {
      const [x, y] = position;
      placed.push({ id: city.id, city, price, name, height, fontSize, anchor: pointAtPixel(position, viewport), x, y, width });
    }
  }
  return placed;
}
