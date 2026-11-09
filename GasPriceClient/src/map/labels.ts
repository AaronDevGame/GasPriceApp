import { LABEL_HEIGHT, LABEL_WIDTH, pointAtPixel, project, rectangleInside } from './geometry';
import type { City, CityPrices, Feature, MapSnapshot, PriceRange } from './types';

export const rangeText = (value: PriceRange | null) => value
  ? `₱${value.minPrice.toFixed(2)}–₱${value.maxPrice.toFixed(2)}/L` : '—';
export type MapLabel = { id: string; city: City; price: CityPrices; x: number; y: number; width: number };
export function placeLabels(snapshot: MapSnapshot, selected: string | null): MapLabel[] {
  const viewport = snapshot.viewport;
  if (!viewport || !snapshot.detail) return [];
  const features = new Map<string, Feature>(snapshot.features.map(feature => [feature.id, feature]));
  const placed: MapLabel[] = [];
  const sorted = [...snapshot.cities].sort((a, b) => Number(b.id === selected) - Number(a.id === selected) || a.id.localeCompare(b.id));
  for (const city of sorted) {
    const price = snapshot.prices.get(city.id), feature = features.get(city.id);
    if (!price || !feature || Date.parse(price.expiresAtUtc) <= Date.now()) continue;
    const [x, y] = project(city.labelPoint, viewport);
    const width = Math.max(LABEL_WIDTH, Math.max(rangeText(price.gasoline).length, rangeText(price.diesel).length) * 6.3 + 16);
    if (x - width / 2 < 8 || x + width / 2 > viewport.width - 8 || y - LABEL_HEIGHT / 2 < 8 || y + LABEL_HEIGHT / 2 > viewport.height - 36) continue;
    const sw = pointAtPixel([x - width / 2, y + LABEL_HEIGHT / 2], viewport);
    const ne = pointAtPixel([x + width / 2, y - LABEL_HEIGHT / 2], viewport);
    if (!rectangleInside(feature, [sw[0], sw[1], ne[0], ne[1]])) continue;
    if (placed.some(other => Math.abs(other.x - x) < (other.width + width) / 2 + 8 && Math.abs(other.y - y) < LABEL_HEIGHT + 8)) continue;
    placed.push({ id: city.id, city, price, x, y, width });
  }
  return placed;
}
