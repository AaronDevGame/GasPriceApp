import { authClient } from '@/auth/session';

export type Area = { city?: string | null; province?: string | null; region?: string | null };
export type PriceRange = { minPrice: number | null; maxPrice: number | null; currency: string; unit: string };
export type Prices = { diesel: PriceRange; gasoline91: PriceRange; gasoline95: PriceRange };
export type PricePoint = { dataAsOf: string; freshness: string; prices: Prices };
export type FeedItem = PricePoint & { area: Area & { level: string; name: string }; isLocal: boolean };
export type Feed = { items: FeedItem[]; localAreaStatus: string };
export type Coordinates = { latitude: number; longitude: number };
export type LocationResult = { result: { location: Area & { resolved_area: string } } };
export type FuelAdjustment = {
  id: number;
  weekStart: string;
  weekEnd: string;
  oilCompany: string;
  effectiveDatePhilippines: string;
  effectiveAtUtc: string | null;
  gasolineChangePerLiter: number | null;
  dieselChangePerLiter: number | null;
  keroseneChangePerLiter: number | null;
  sourceUrl: string;
  fetchedAtUtc: string;
};
export type FuelAdjustmentWeek = {
  weekStart: string;
  weekEnd: string;
  adjustments: FuelAdjustment[];
};
export type FuelAdjustmentFeed = {
  weeks: number;
  requestedWeeks: number;
  groups: FuelAdjustmentWeek[];
  items: FuelAdjustment[];
};

export function getFuelAdjustments() {
  return authClient.request<FuelAdjustmentFeed>('/fuel-prices/adjustments?weeks=5');
}

export function areaQuery(area?: Area) {
  const params = new URLSearchParams();
  for (const key of ['city', 'province', 'region'] as const) {
    if (area?.[key]?.trim()) params.set(key, area[key]!.trim());
  }
  return params;
}

export function getPrices(area?: Area) {
  const query = areaQuery(area).toString();
  return authClient.request<Feed>(`/fuel-prices${query ? `?${query}` : ''}`);
}

export async function refreshLocation(coordinates: Coordinates) {
  const result = await authClient.request<LocationResult>('/ai/fuel-prices', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(coordinates),
  });
  const area = result.result.location;
  if (!areaQuery(area).size) throw new Error('No area resolved');
  return { area, feed: await getPrices(area) };
}

export async function getHistory(item: FeedItem) {
  const params = areaQuery(item.area);
  params.set('scope', item.area.level);
  params.set('limit', '100');
  const history = await authClient.request<{ items: PricePoint[] }>(`/fuel-prices/history?${params}`);
  return meaningfulUpdates(history.items);
}

function signature(point: PricePoint) {
  return ['diesel', 'gasoline91', 'gasoline95'].map(key => {
    const range = point.prices[key as keyof Prices];
    return [range.minPrice, range.maxPrice, range.currency, range.unit].join(':');
  }).join('|');
}

// A snapshot is an update only if it differs from the preceding observation.
export function meaningfulUpdates(points: PricePoint[]) {
  const sorted = [...points].sort((a, b) => Date.parse(b.dataAsOf) - Date.parse(a.dataAsOf));
  return sorted.filter((point, index) => index + 1 < sorted.length &&
    signature(point) !== signature(sorted[index + 1])).slice(0, 5);
}

export function formatPrice(range: PriceRange) {
  const { minPrice: min, maxPrice: max } = range;
  if (min === null && max === null) return 'Not available';
  if (min !== null && max !== null && min !== max) return `₱${min.toFixed(2)}–₱${max.toFixed(2)}`;
  return `₱${(min ?? max)!.toFixed(2)}`;
}
