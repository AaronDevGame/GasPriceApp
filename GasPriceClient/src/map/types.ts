export type Bounds = [number, number, number, number]; // west, south, east, north
export type Point = [number, number]; // longitude, latitude
export type City = {
  id: string; name: string; level: 'City' | 'Mun'; cityClass: string | null;
  regionId: string; regionName: string; provinceId: string | null; groupId: string; groupName: string;
  bbox: Bounds; labelPoint: Point; fileId: string;
};
export type GeographicIndex = {
  version: string; countryCode: 'PH'; defaultCityId: string; bbox: Bounds;
  attribution: string; licenseUrl: string;
  cities: City[]; files: { id: string; bbox: Bounds; cityIds: string[] }[];
};
export type PolygonGeometry = { type: 'Polygon'; coordinates: Point[][] } |
  { type: 'MultiPolygon'; coordinates: Point[][][] };
export type Feature = { type: 'Feature'; id: string; properties: { cityId: string } | { areaId: string }; geometry: PolygonGeometry };
export type BoundaryFile = { type: 'FeatureCollection'; features: Feature[] };
export type PriceRange = { minPrice: number; maxPrice: number; currency: 'PHP'; unit: 'liter' };
export type CityPrices = {
  cityId: string; scope: 'city'; status: 'available' | 'partial' | 'unavailable'; gasolineGrade: 'RON 91';
  gasoline: PriceRange | null; diesel: PriceRange | null; expiresAtUtc: string;
};
export type PriceFeed = {
  boundaryVersion: string; gasolineGrade: 'RON 91'; dieselGrade: 'DIESEL'; generatedAtUtc: string;
  expiresAtUtc: string; items: CityPrices[];
};
export type Viewport = { bounds: Bounds; zoom: number; width: number; height: number };
export type MapSnapshot = {
  index: GeographicIndex | null; cities: City[]; features: Feature[];
  prices: ReadonlyMap<string, CityPrices>; viewport: Viewport | null;
  loading: boolean; detail: boolean; error: string | null;
};
export type MapRendererProps = {
  snapshot: MapSnapshot; selected: string | null;
  onMove(): void; onIdle(viewport: Viewport): void; onSelect(id: string): void;
};
