import type { DoeFuelPriceFeed, DoePriceLocation, FeedItem } from './api';
import { mapAreas, type MapArea } from '@/map/hierarchy';
import type { City, GeographicIndex } from '@/map/types';

// Geographic boundary and DOE report names use different City/Province affixes.
export function areaName(value: string | null | undefined) {
  return (value ?? '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase()
    .replace(/\b(city of|municipality of|province of)\b/g, '').replace(/\b(city|province)\b/g, '')
    .replace(/[^a-z0-9]/g, '');
}
function cityMatchesProvince(city: City, province: string) {
  const name = areaName(province);
  return areaName(city.groupName) === name || areaName(city.regionName) === name ||
    (city.regionId === '1300000000' && ['metromanila', 'ncr', 'nationalcapitalregion', 'nationalcapitalregionncr'].includes(name));
}
export function sameLocation(a: DoePriceLocation, b: DoePriceLocation) {
  return areaName(a.city) === areaName(b.city) && areaName(a.province) === areaName(b.province);
}
export function localLocation(item: FeedItem, report: DoeFuelPriceFeed | null): DoePriceLocation | null {
  if (report?.province && report.prices.length) return {
    city: report.city ?? '', province: report.province, scope: report.scope ?? undefined,
  };
  return item.area.province ? { city: item.area.city ?? '', province: item.area.province } : null;
}
export function mapAreaForLocation(index: GeographicIndex, location: DoePriceLocation): MapArea | null {
  const matches = mapAreas(index, location.city ? 'city' : 'province').filter(area => {
    if (!location.city) return area.id.startsWith('province:') && areaName(area.name) === areaName(location.province);
    return areaName(area.name) === areaName(location.city) && area.members.some(city =>
      cityMatchesProvince(city, location.province));
  });
  // Never choose an ambiguous city in another province.
  return matches.length === 1 ? matches[0] : null;
}
export function locationForMapArea(area: MapArea, locations: DoePriceLocation[]): DoePriceLocation | null {
  const matches = locations.filter(location => {
    if (area.id.startsWith('province:')) return !location.city && areaName(location.province) === areaName(area.name);
    if (area.members.length !== 1 || area.id.includes(':')) return false;
    return areaName(location.city) === areaName(area.name) &&
      cityMatchesProvince(area.members[0], location.province);
  });
  return matches.length === 1 ? matches[0] : null;
}
