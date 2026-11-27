import data from './data/fuel-stations.json';
import type { Viewport } from './types';

type Station = { id: string; name: string; lat: number; lng: number };
type Group = Station & { count: number };

let cachedClusters: { zoom: number; groups: Group[] } | undefined;
const stationPins: Group[] = data.map(station => ({ ...station, count: 1 }));

function clustersAtZoom(zoom: number): Group[] {
  if (cachedClusters?.zoom === zoom) return cachedClusters.groups;
  const groups = new Map<string, Group>();
  const cell = 360 * 64 / (256 * 2 ** zoom);
  for (const station of stationPins) {
    const id = Math.floor(station.lng / cell) + '/' + Math.floor(station.lat / cell);
    const group = groups.get(id);
    if (group) {
      group.count++;
      group.lat += (station.lat - group.lat) / group.count;
      group.lng += (station.lng - group.lng) / group.count;
    } else groups.set(id, { ...station, id });
  }
  const clusters = [...groups.values()];
  cachedClusters = { zoom, groups: clusters };
  return clusters;
}

export function stationMarkers(view: Viewport | null): Group[] {
  if (!view) return [];
  const [west, south, east, north] = view.bounds;
  const markers = view.zoom >= 15 ? stationPins : clustersAtZoom(Math.floor(view.zoom));
  return markers.filter(({ lng, lat }) => lng >= west && lng <= east && lat >= south && lat <= north);
}

export function stationDirections(station: Station) {
  return 'https://www.google.com/maps/dir/?api=1&destination=' + station.lat + ',' + station.lng;
}
