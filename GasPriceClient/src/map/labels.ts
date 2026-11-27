import { boundsCenter, pointAtPixel, project } from './geometry';
import { areaPrices, mapLevel, visibleAreas, type MapArea } from './hierarchy';
import type { CityPrices, MapSnapshot, Point, PriceRange, Viewport } from './types';

export const mapLabelColors = {
  unselected: {
    background: 'rgba(8, 22, 38, 0.22)',
    gradient: 'linear-gradient(145deg, rgba(10, 24, 41, 0.86) 0%, rgba(16, 36, 56, 0.64) 48%, rgba(36, 67, 91, 0.12) 100%)',
    border: '#6CB4F8',
  },
  selected: { background: '#115442', border: '#69E0B3' },
  unavailable: { background: 'rgba(60, 68, 77, 0.87)', border: '#9AA5B1', text: '#E7EBF0' },
  skeleton: { background: 'rgba(17, 31, 48, 0.92)', border: '#7997B5', bar: '#58718C' },
  textShadow: '#071322',
};

export const rangeText = (value: PriceRange | null) => value
  ? `₱${value.minPrice.toFixed(2)}–₱${value.maxPrice.toFixed(2)}/L` : '—';
export const reportDate = (value: string) => new Date(`${value}T00:00:00+08:00`).toLocaleDateString('en-PH', {
  month: 'short', day: 'numeric', timeZone: 'Asia/Manila',
});
export function adjustmentSummaries(adjustments: NonNullable<import('./types').CityPrices['adjustments']>) {
  const groups = new Map<string, { fuel: string; date: string; amount: number; companies: Set<string> }>();
  for (const change of adjustments) {
    const key = `${change.fuel}:${change.effectiveDate}:${change.changePerLiter}`;
    if (!groups.has(key)) groups.set(key, { fuel: change.fuel, date: change.effectiveDate, amount: change.changePerLiter, companies: new Set() });
    groups.get(key)!.companies.add(change.oilCompany ?? 'All companies');
  }
  return [...groups.values()].sort((a, b) => a.date.localeCompare(b.date) || a.fuel.localeCompare(b.fuel));
}
export function cityPriceLabel(report: CityPrices) {
  if (report.priceKind === 'estimated') return `Estimated${report.asOf ? ` · ${reportDate(report.asOf)}` : ''}`;
  const dates = report.weekStart && report.weekEnd ? `${reportDate(report.weekStart)}–${reportDate(report.weekEnd)}` : report.weekEnd ? reportDate(report.weekEnd) : '';
  return `DOE reported${dates ? ` · ${dates}` : ''}${report.isOutdated ? ' · Outdated' : ''}`;
}
export function priceLabel(price: ReturnType<typeof areaPrices>) {
  if (!price.reported) return 'No recent report';
  if (price.report) return cityPriceLabel(price.report);
  return `${price.estimated ? 'Includes estimates' : 'DOE reported'}${price.outdated ? ' · Outdated reports' : ''}`;
}
export type MapLabel = {
  id: string; name: string; priceLines: string[]; skeletonWidths: readonly number[]; coverage: string; accessibilityLabel: string;
  price: ReturnType<typeof areaPrices>; center: Point; anchor: Point; x: number; y: number; width: number; height: number; fontSize: number;
};
const cardInset = 8;
const cardBottomInset = 36;
// Search the part of the city's bounding box where a full label can fit on screen.
function* candidates(city: MapArea, viewport: Viewport, width: number, height: number): Generator<Point> {
  const nw = project([city.bbox[0], city.bbox[3]], viewport);
  const se = project([city.bbox[2], city.bbox[1]], viewport);
  const left = Math.max(cardInset + width / 2, nw[0]), right = Math.min(viewport.width - cardInset - width / 2, se[0]);
  const top = Math.max(cardInset + height / 2, nw[1]), bottom = Math.min(viewport.height - cardBottomInset - height / 2, se[1]);
  if (viewport.width < width + 2 * cardInset || viewport.height < height + cardInset + cardBottomInset) return;
  if (nw[0] > viewport.width || se[0] < 0 || nw[1] > viewport.height || se[1] < 0) return;
  if (left > right || top > bottom) {
    yield [Math.max(cardInset + width / 2, Math.min(viewport.width - cardInset - width / 2, (nw[0] + se[0]) / 2)),
      Math.max(cardInset + height / 2, Math.min(viewport.height - cardBottomInset - height / 2, (nw[1] + se[1]) / 2))];
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
export function placeLabels(snapshot: MapSnapshot, selected: string | null, previousAnchors?: ReadonlyMap<string, Point>): MapLabel[] {
  const viewport = snapshot.viewport;
  if (!viewport) return [];
  const placed: MapLabel[] = [];
  if (!snapshot.index) return [];
  const level = mapLevel(snapshot.index, viewport);
  const areas = visibleAreas(snapshot.index, viewport);
  const sorted = areas.map(area => {
    const price = areaPrices(area, snapshot);
    const pending = !price.gasoline && !price.diesel && area.members.some(city => snapshot.pendingPriceIds.has(city.id));
    return { area, price, pending };
  }).sort((a, b) =>
    Number(b.area.id === selected) - Number(a.area.id === selected) ||
    Number(!!(b.price.gasoline || b.price.diesel)) - Number(!!(a.price.gasoline || a.price.diesel)) ||
    Number(b.pending) - Number(a.pending) || a.area.id.localeCompare(b.area.id));
  for (const { area, price, pending } of sorted) {
    const name = area.name;
    const fontSize = 11;
    const priceRows = [{ fuel: 'RON 91', range: price.gasoline }, { fuel: 'Diesel', range: price.diesel }]
      .flatMap(({ fuel, range }) => range ? [{ fuel, text: rangeText(range) }] : []);
    const priceLines = priceRows.map(({ fuel, text }) => priceRows.length === 1 ? `${fuel} ${text}` : text);
    const skeletonWidths = pending ? (level === 'city' ? [74, 62, 46] : [74, 62, 46, 58]) : [];
    const coverage = pending ? '' : [priceLabel(price), !priceLines.length ? '' : level === 'city' ? (price.complete ? '' : 'Incomplete') :
      `${price.reported}/${price.total} cities with prices${price.complete ? '' : ' · incomplete'}`].filter(Boolean).join('\n');
    const accessibilityLabel = pending ? `${name}, loading fuel prices` :
      [name, ...priceRows.map(({ fuel, text }) => `${fuel} ${text}`), coverage].join(', ');
    const lines = [name, ...priceLines, ...coverage.split('\n')];
    const height = 14 + (pending ? 1 + skeletonWidths.length : lines.length) * 16;
    const longestLine = Math.max(...lines.map(line => line.length));
    const width = Math.min(viewport.width - 16, Math.max(pending ? 154 : 130, longestLine * fontSize * 0.64 + 18));
    const positions = [...candidates(area, viewport, width, height)];
    const previousAnchor = previousAnchors?.get(area.id);
    let previousPosition = previousAnchor && project(previousAnchor, viewport);
    let keepPreviousAnchor = false;
    if (positions.length && previousPosition) {
      const [x, y] = previousPosition;
      const insetX = Math.max(cardInset + width / 2, Math.min(x, viewport.width - cardInset - width / 2));
      const insetY = Math.max(cardInset + height / 2, Math.min(y, viewport.height - cardBottomInset - height / 2));
      keepPreviousAnchor = insetX === x && insetY === y;
      previousPosition = [insetX, insetY];
      positions.unshift(previousPosition);
    }
    const position = positions.find(([x, y]) => !placed.some(other =>
      Math.abs(other.x - x) < (other.width + width) / 2 + 8 && Math.abs(other.y - y) < (other.height + height) / 2 + 8));
    // Selected areas go first. Never force a colliding card into the remaining space.
    if (position) {
      const [x, y] = position;
      placed.push({ id: area.id, price, priceLines, skeletonWidths, coverage, accessibilityLabel, name, height, fontSize,
        center: boundsCenter(area.bbox), anchor: previousAnchor && keepPreviousAnchor && position === previousPosition
          ? previousAnchor : pointAtPixel(position, viewport), x, y, width });
    }
  }
  return placed;
}
