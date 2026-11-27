import { boundsCenter, pointAtPixel, project } from './geometry';
import { areaPrices, mapLevel, visibleAreas } from './hierarchy';
import type { Bounds, CityPrices, MapSnapshot, Point, PriceRange, Viewport } from './types';

export const mapLabelColors = {
  selected: { background: '#116453', border: '#92F4C8' },
  neutral: { background: 'rgba(60, 68, 77, 0.87)', border: '#9AA5B1' },
  unavailableText: '#E7EBF0',
  skeleton: { background: 'rgba(17, 31, 48, 0.92)', border: '#7997B5', bar: '#58718C' },
  textShadow: '#071322',
};
export const mapLabelNozzle = { size: 14, gap: 4 };

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
const fuelNames = { gasoline: 'RON 91', diesel: 'Diesel' };
const mapFuels = ['gasoline', 'diesel'] as const;
export type MapLabel = {
  id: string; name: string; priceRows: { fuel: typeof mapFuels[number]; text: string }[];
  skeletonWidths: readonly number[]; coverage: string; accessibilityLabel: string;
  center: Point; labelPoint: Point; bounds: Bounds; width: number; height: number; fontSize: number;
};
export type LabelRectangle = { x: number; y: number; width: number; height: number };
type LabelViewport = Pick<Viewport, 'width' | 'height'>;
const cardInset = 8;
const cardBottomInset = 36;
export function clampLabelCenter([x, y]: Point, viewport: LabelViewport,
  width: number, height: number): Point {
  return [
    Math.max(cardInset + width / 2, Math.min(x, viewport.width - cardInset - width / 2)),
    Math.max(cardInset + height / 2, Math.min(y, viewport.height - cardBottomInset - height / 2)),
  ];
}
function touchesCardMargin([x, y]: Point, viewport: LabelViewport, width: number, height: number) {
  return x <= cardInset + width / 2 || x >= viewport.width - cardInset - width / 2 ||
    y <= cardInset + height / 2 || y >= viewport.height - cardBottomInset - height / 2;
}

function overlapsLabel(x: number, y: number, width: number, height: number, placed: readonly LabelRectangle[]) {
  return placed.some(other =>
    Math.abs(other.x - x) < (other.width + width) / 2 + 8 && Math.abs(other.y - y) < (other.height + height) / 2 + 8);
}
// Search the part of the city's bounding box where a full label can fit on screen.
function* candidates([x, y]: Point, nw: Point, se: Point, viewport: LabelViewport,
  width: number, height: number): Generator<Point> {
  const left = Math.max(cardInset + width / 2, nw[0]), right = Math.min(viewport.width - cardInset - width / 2, se[0]);
  const top = Math.max(cardInset + height / 2, nw[1]), bottom = Math.min(viewport.height - cardBottomInset - height / 2, se[1]);
  if (left > right || top > bottom) {
    yield clampLabelCenter([(nw[0] + se[0]) / 2, (nw[1] + se[1]) / 2], viewport, width, height);
    return;
  }
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
function projectLabel(label: MapLabel, viewport: LabelViewport, projectPoint: (point: Point) => Point | null) {
  const { width, height, bounds, labelPoint } = label;
  if (viewport.width < width + 2 * cardInset || viewport.height < height + cardInset + cardBottomInset) return null;
  const nw = projectPoint([bounds[0], bounds[3]]);
  const se = projectPoint([bounds[2], bounds[1]]);
  if (!nw || !se || nw[0] > viewport.width || se[0] < 0 || nw[1] > viewport.height || se[1] < 0) return null;
  const preferred = projectPoint(labelPoint);
  return preferred && { preferred, nw, se };
}

export function webLabelPosition(label: MapLabel, viewport: LabelViewport, projectPoint: (point: Point) => Point | null,
  placed: readonly LabelRectangle[], priority: number, cardCount: number, previousOffset?: Point):
  (LabelRectangle & { zIndex: number; edgePushed: boolean; offset?: Point }) | null {
  const projected = projectLabel(label, viewport, projectPoint);
  if (!projected) return null;
  const { preferred } = projected;
  const { width, height } = label;
  const [x, y] = clampLabelCenter(preferred, viewport, width, height);
  const zIndex = cardCount - priority;
  if (touchesCardMargin([x, y], viewport, width, height)) {
    return { x, y, width, height, zIndex, edgePushed: true };
  }
  let position: LabelRectangle | null = null;
  if (previousOffset) {
    const priorX = preferred[0] + previousOffset[0];
    const priorY = preferred[1] + previousOffset[1];
    if (!touchesCardMargin([priorX, priorY], viewport, width, height) &&
      !overlapsLabel(priorX, priorY, width, height, placed)) {
      position = { x: priorX, y: priorY, width, height };
    }
  }
  position ??= findLabelPosition(label, viewport, projected, placed);
  if (!position) return null;
  const edgePushed = touchesCardMargin([position.x, position.y], viewport, width, height);
  const offset: Point = [position.x - preferred[0], position.y - preferred[1]];
  return { ...position, zIndex: edgePushed ? zIndex : zIndex + cardCount, edgePushed,
    offset: !edgePushed && (offset[0] || offset[1]) ? offset : undefined };
}

function findLabelPosition(label: MapLabel, viewport: LabelViewport, projected: NonNullable<ReturnType<typeof projectLabel>>,
  placed: readonly LabelRectangle[]): LabelRectangle | null {
  const { width, height } = label;
  const { preferred, nw, se } = projected;
  for (const [x, y] of candidates(preferred, nw, se, viewport, width, height)) {
    if (!overlapsLabel(x, y, width, height, placed)) {
      return { x, y, width, height };
    }
  }
  return null;
}

export function mapLabels(snapshot: MapSnapshot, selected: string | null): MapLabel[] {
  const viewport = snapshot.viewport;
  if (!viewport) return [];
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
  return sorted.map(({ area, price, pending }) => {
    const name = area.name;
    const fontSize = 11;
    const isSelected = area.id === selected;
    const prices = mapFuels.flatMap(fuel => {
      const range = price[fuel];
      return range ? [{ fuel, amount: isSelected ? rangeText(range) : `₱${range.minPrice.toFixed(2)}` }] : [];
    });
    const compact = !isSelected && prices.length > 0;
    const priceRows = prices.map(({ fuel, amount }) => ({
      fuel, text: isSelected && prices.length === 1 ? `${fuelNames[fuel]} ${amount}` : amount,
    }));
    const skeletonWidths = pending ? (level === 'city' ? [74, 62, 46] : [74, 62, 46, 58]) : [];
    const coverage = pending || compact ? '' : [priceLabel(price), !prices.length ? '' : level === 'city' ? (price.complete ? '' : 'Incomplete') :
      `${price.reported}/${price.total} cities with prices${price.complete ? '' : ' · incomplete'}`].filter(Boolean).join('\n');
    const accessibilityLabel = pending ? `${name}, loading fuel prices` :
      [name, ...prices.map(({ fuel, amount }) => compact ?
        `${fuelNames[fuel]} lowest price ${amount} per liter` : `${fuelNames[fuel]} ${amount}`), coverage].filter(Boolean).join(', ');
    const coverageLines = coverage ? coverage.split('\n') : [];
    const height = 14 + (1 + (pending ? skeletonWidths.length : priceRows.length + coverageLines.length)) * 16;
    const textWidth = (line: string) => line.length * fontSize * 0.64;
    const contentWidth = Math.max(textWidth(name), ...coverageLines.map(textWidth),
      ...priceRows.map(({ text }) => mapLabelNozzle.size + mapLabelNozzle.gap + textWidth(text)));
    const width = Math.min(viewport.width - 16, Math.max(pending ? 154 : 130, contentWidth + 18));
    return { id: area.id, priceRows, skeletonWidths, coverage, accessibilityLabel, name, height, fontSize,
      center: boundsCenter(area.bbox), labelPoint: area.labelPoint, bounds: area.bbox, width };
  });
}

export function placeLabels(snapshot: MapSnapshot, selected: string | null) {
  const placed: (MapLabel & LabelRectangle & { anchor: Point })[] = [];
  const viewport = snapshot.viewport;
  if (!viewport) return placed;
  const projectPoint = (point: Point) => project(point, viewport);
  for (const label of mapLabels(snapshot, selected)) {
    const projected = projectLabel(label, viewport, projectPoint);
    const position = projected && findLabelPosition(label, viewport, projected, placed);
    if (position) placed.push({ ...label, ...position, anchor: pointAtPixel([position.x, position.y], viewport) });
  }
  return placed;
}
