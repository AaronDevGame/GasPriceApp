import type { MapLevel } from './hierarchy';
import type { Feature, Point } from './types';

// The pack stores one encoded polyline per ring to keep wide views cheap to load.
type AreaPack = { version: string; areas: Record<string, string[][]> };
const decoded = new Map<string, Feature>();

function decodeRing(encoded: string): Point[] {
  const ring: Point[] = [];
  let offset = 0;
  let latitude = 0;
  let longitude = 0;
  function delta() {
    let bits = 0, shift = 0, value: number;
    do {
      value = encoded.charCodeAt(offset++) - 63;
      bits |= (value & 31) << shift;
      shift += 5;
    } while (value >= 32);
    return bits & 1 ? ~(bits >>> 1) : bits >>> 1;
  }
  while (offset < encoded.length) {
    latitude += delta();
    longitude += delta();
    ring.push([longitude / 100000, latitude / 100000]);
  }
  return ring;
}

export function areaBoundaries(level: MapLevel, version: string) {
  if (level !== 'island' && level !== 'region' && level !== 'province') return null;
  const pack: AreaPack = require('./data/area-boundaries.json');
  if (pack.version !== version) return null;
  return {
    get(id: string): Feature | undefined {
      const existing = decoded.get(id);
      if (existing) return existing;
      const rings = pack.areas[id];
      if (!rings) return undefined;
      const parts = rings.map(polygon => polygon.map(decodeRing));
      const feature: Feature = { type: 'Feature', id, properties: { areaId: id },
        geometry: parts.length === 1
          ? { type: 'Polygon', coordinates: parts[0] }
          : { type: 'MultiPolygon', coordinates: parts },
      };
      decoded.set(id, feature);
      return feature;
    },
  };
}
