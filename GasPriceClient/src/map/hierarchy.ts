import { fitZoom, intersects, midpoint } from './geometry';
import type { Bounds, City, CityPrices, GeographicIndex, MapSnapshot, PriceRange, Viewport } from './types';

export type MapLevel = 'country' | 'island' | 'region' | 'province' | 'city';
export type MapArea = { id: string; name: string; bbox: Bounds; labelPoint: [number, number]; members: City[] };
export const levelNames: Record<MapLevel, string> = {
  country: 'Nationwide', island: 'Island groups', region: 'Regions', province: 'Provinces / independent cities', city: 'Cities / municipalities',
};
// Relative to the national fit so the hierarchy also works on narrow phone screens.
export function mapLevel(index: GeographicIndex, viewport: Viewport): MapLevel {
  const delta = viewport.zoom - fitZoom(index.bbox, viewport.width, viewport.height, 35);
  return delta < 0.75 ? 'country' : delta < 2 ? 'island' : delta < 3.5 ? 'region' : delta < 5 ? 'province' : 'city';
}
export function areaIdentity(city: City, level: MapLevel): [string, string] {
  if (level === 'country') return ['country:PH', 'Philippines'];
  if (level === 'island') {
    const region = city.regionId.slice(0, 2);
    const name = ['06', '07', '08', '18'].includes(region) ? 'Visayas'
      : ['09', '10', '11', '12', '16', '19'].includes(region) ? 'Mindanao' : 'Luzon';
    return [`island:${name}`, name];
  }
  if (level === 'region') return [`region:${city.regionId}`, city.regionName];
  // Independent cities have no administrative province. Never assign their storage group as one.
  if (level === 'province' && city.provinceId) return [`province:${city.provinceId}`, city.groupName];
  return [city.id, city.name.trim() || city.groupName];
}
export function mapAreas(index: GeographicIndex, level: MapLevel): MapArea[] {
  const areas = new Map<string, MapArea>();
  for (const city of index.cities) {
    const [id, name] = areaIdentity(city, level);
    let area = areas.get(id);
    if (!area) {
      area = { id, name, bbox: [...city.bbox], labelPoint: city.labelPoint, members: [] };
      areas.set(id, area);
    }
    area.members.push(city);
    area.bbox = [Math.min(area.bbox[0], city.bbox[0]), Math.min(area.bbox[1], city.bbox[1]),
      Math.max(area.bbox[2], city.bbox[2]), Math.max(area.bbox[3], city.bbox[3])];
  }
  for (const area of areas.values()) if (area.members.length > 1) {
    area.labelPoint = area.id === 'country:PH' ? [122, 12.5] : area.id === 'island:Luzon' ? [121, 16]
      : area.id === 'island:Visayas' ? [123.3, 11.3] : area.id === 'island:Mindanao' ? [125, 7.5] : midpoint(area.bbox);
  }
  return [...areas.values()];
}
export function visibleAreas(index: GeographicIndex, viewport: Viewport) {
  return mapAreas(index, mapLevel(index, viewport)).filter(area => intersects(area.bbox, viewport.bounds));
}
export function areaPrices(area: MapArea, snapshot: Pick<MapSnapshot, 'prices'>) {
  const reports = area.members.map(city => snapshot.prices.get(city.id))
    .filter((price): price is CityPrices => !!price && Date.parse(price.expiresAtUtc) > Date.now());
  const range = (grade: 'gasoline' | 'diesel'): PriceRange | null => {
    const values = reports.flatMap(report => report[grade] ? [report[grade]!] : []);
    return values.length ? { minPrice: Math.min(...values.map(v => v.minPrice)), maxPrice: Math.max(...values.map(v => v.maxPrice)), currency: 'PHP', unit: 'liter' } : null;
  };
  return { gasoline: range('gasoline'), diesel: range('diesel'), reported: reports.filter(p => p.gasoline || p.diesel).length,
    total: area.members.length, complete: reports.length === area.members.length };
}
