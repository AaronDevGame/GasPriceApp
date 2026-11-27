import data from './data/fuel-stations.json';
import type { Viewport } from './types';

type Station = { id: string; name: string; lat: number; lng: number };
type Group = Station & { count: number };

export function stationMarkers(view: Viewport | null): Group[] {
  if (!view) return [];
  const [west, south, east, north] = view.bounds;
  const groups = new Map<string, Group>();
  const cell = 360 * 64 / (256 * 2 ** Math.floor(view.zoom));
  for (const station of data as Station[]) {
    if (station.lng < west || station.lng > east || station.lat < south || station.lat > north) continue;
    const id = view.zoom >= 15 ? station.id : Math.floor(station.lng / cell) + '/' + Math.floor(station.lat / cell);
    const group = groups.get(id);
    if (group) {
      group.count++;
      group.lat += (station.lat - group.lat) / group.count;
      group.lng += (station.lng - group.lng) / group.count;
    } else groups.set(id, { ...station, id, count: 1 });
  }
  return [...groups.values()];
}

export function stationDirections(station: Station) {
  return 'https://www.google.com/maps/dir/?api=1&destination=' + station.lat + ',' + station.lng;
}
