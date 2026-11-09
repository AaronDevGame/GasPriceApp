import { apiBase, ApiError } from '@/auth/http';
import { authClient } from '@/auth/session';
import type { BoundaryFile, GeographicIndex, PriceFeed } from './types';

const prefix = '/fuel-prices/map';
async function geography<T>(path: string): Promise<T> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 30000);
  try {
    // Public geometry has its own transport: no user credentials, no auth queue, HTTP caching enabled.
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await fetch(`${apiBase()}${path}`, {
        headers: { Accept: 'application/json' }, credentials: 'omit', redirect: 'error', signal: controller.signal,
      });
      if (response.status === 429 && attempt === 0) {
        const seconds = Number(response.headers.get('Retry-After'));
        if (Number.isFinite(seconds) && seconds > 0 && seconds <= 5) {
          await new Promise(resolve => setTimeout(resolve, seconds * 1000)); continue;
        }
      }
      if (!response.ok) throw new ApiError(response.status, 'map_geography_unavailable');
      const envelope = await response.json();
      if (!envelope?.data) throw new ApiError(0, 'invalid_map_response');
      return envelope.data as T;
    }
    throw new ApiError(429, 'map_rate_limited');
  } finally { clearTimeout(timeout); }
}
export const mapApi = {
  index: () => geography<GeographicIndex>(`${prefix}/geography/index`),
  boundary: (version: string, file: string) => {
    if (!/^ph-[a-z0-9-]+$/.test(version) || !/^\d{10}-\d{2}$/.test(file)) throw new Error('Invalid map file');
    return geography<BoundaryFile>(`${prefix}/geography/${version}/${file}`);
  },
  prices: (boundaryVersion: string, cityIds: string[]) => authClient.request<PriceFeed>(`${prefix}/prices`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ boundaryVersion, cityIds }),
  }),
};
