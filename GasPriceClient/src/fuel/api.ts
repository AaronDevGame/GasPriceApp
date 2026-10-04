import { authClient } from '@/auth/session';

export type Area = { city?: string | null; province?: string | null; region?: string | null };
export type PriceRange = { minPrice: number | null; maxPrice: number | null; currency: string; unit: string };
export type Prices = { diesel: PriceRange; gasoline91: PriceRange; gasoline95: PriceRange };
export type PricePoint = { dataAsOf: string; freshness: string; prices: Prices };
export type FeedItem = PricePoint & { area: Area & { level: string; name: string }; isLocal: boolean };
export type Feed = { items: FeedItem[]; localAreaStatus: string };
export type FeaturedFuelPrice = {
  city: string; province: string; reportWeekStart: string; reportWeekEnd: string;
  dataAsOf: string; prices: Prices;
  source: { name: string; url: string; publishedAt: string | null; geographicCoverage: string };
};
export type FeaturedFuelPriceGroup = { name: string; items: FeaturedFuelPrice[] };
export type FeaturedFuelPriceFeed = { groups: FeaturedFuelPriceGroup[] };
export type Coordinates = { latitude: number; longitude: number };
export type DoeFuelPrice = {
  city: string; province: string; region: string | null; oilCompany: string;
  fuelGrade: string; minPricePerLiter: number; maxPricePerLiter: number;
  weekStart: string; weekEnd: string; sourceUrl: string; fetchedAtUtc: string;
};
export type DoeFuelPriceFeed = {
  city: string | null; province: string | null; region: string | null;
  weekStart: string | null; weekEnd: string | null; prices: DoeFuelPrice[];
};
export type DoePriceLocation = { city: string; province: string };
export type DoePriceBrowseFeed = {
  locations: DoePriceLocation[]; selected: DoeFuelPriceFeed | null;
};

export function getDoePriceBrowse(location?: DoePriceLocation) {
  const query = location ? `?${new URLSearchParams(location)}` : '';
  return authClient.request<DoePriceBrowseFeed>(`/fuel-prices/doe/browse${query}`);
}

export function cheapestDoePrices(feed: DoeFuelPriceFeed, grade: string): DoeFuelPrice[] {
  return feed.prices.filter(price => price.fuelGrade === grade &&
    Number.isFinite(price.minPricePerLiter) && price.minPricePerLiter > 0 &&
    Number.isFinite(price.maxPricePerLiter) && price.maxPricePerLiter >= price.minPricePerLiter)
    .sort((a, b) => a.minPricePerLiter - b.minPricePerLiter ||
      a.maxPricePerLiter - b.maxPricePerLiter || a.oilCompany.localeCompare(b.oilCompany));
}
export type LocationResult = {
  result: {
    location: Area & { resolved_area: string };
    estimate_area?: { level: string; name: string };
    data_as_of?: string | null;
    prices?: Record<'diesel' | 'gasoline_91' | 'gasoline_95', {
      min_price: number | null; max_price: number | null; currency: string; unit: string;
    }>;
  };
  doePrices?: DoeFuelPriceFeed | null;
};
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
};

export function getFuelAdjustments() {
  return authClient.request<FuelAdjustmentFeed>('/fuel-prices/adjustments?weeks=52');
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

export function getFeaturedPrices() {
  return authClient.request<FeaturedFuelPriceFeed>('/fuel-prices/featured');
}

export function getDoePrices(coordinates: Coordinates) {
  const params = new URLSearchParams({
    latitude: String(coordinates.latitude), longitude: String(coordinates.longitude),
  });
  return authClient.request<DoeFuelPriceFeed>(`/fuel-prices/doe?${params}`);
}

export async function refreshLocation(coordinates: Coordinates) {
  const result = await authClient.request<LocationResult>('/ai/fuel-prices', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(coordinates),
  });
  const area = result.result.location;
  if (!areaQuery(area).size) throw new Error('No area resolved');
  const feed = await getPrices(area);
  const local = localPriceItem(result.result);
  const localizedFeed = local ? {
    ...feed,
    items: [local, ...feed.items.filter(item =>
      item.area.level !== local.area.level || item.area.name !== local.area.name ||
      item.area.province !== local.area.province || item.area.region !== local.area.region
    ).map(item => ({ ...item, isLocal: false }))].slice(0, 10),
    localAreaStatus: 'available',
  } : feed;
  let doePrices = result.doePrices ?? null;
  if (!doePrices?.prices.length) {
    // Research cache entries from other sources may predate newly imported DOE rows.
    try { doePrices = await getDoePrices(coordinates); } catch { /* Keep the local estimate available. */ }
  }
  return { area, feed: localizedFeed, doePrices };
}

function localPriceItem(result: LocationResult['result']): FeedItem | null {
  const source = result.prices;
  const estimate = result.estimate_area;
  if (!source || !estimate?.name || !result.data_as_of) return null;
  const convert = (range: typeof source.diesel): PriceRange | null => {
    if (!range || ![range.min_price, range.max_price].every(value =>
      value === null || (typeof value === 'number' && Number.isFinite(value) && value >= 0)) ||
      (range.min_price !== null && range.max_price !== null && range.min_price > range.max_price)) return null;
    return { minPrice: range.min_price, maxPrice: range.max_price,
      currency: range.currency, unit: range.unit };
  };
  const diesel = convert(source.diesel);
  const gasoline91 = convert(source.gasoline_91);
  const gasoline95 = convert(source.gasoline_95);
  if (!diesel || !gasoline91 || !gasoline95 ||
      [diesel, gasoline91, gasoline95].every(price => price.minPrice === null && price.maxPrice === null)) return null;
  return {
    area: { ...result.location, level: estimate.level, name: estimate.name },
    isLocal: true, dataAsOf: result.data_as_of, freshness: 'current',
    prices: { diesel, gasoline91, gasoline95 },
  };
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
