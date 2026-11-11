import { MapDataCache, mapCache } from './cache';
import { areaBoundaries } from './area-boundaries';
import { MAX_DETAIL_CITIES } from './geometry';
import { mapLevel, visibleAreas } from './hierarchy';
import type { MapSnapshot, Viewport } from './types';

export class MapController {
  snapshot: MapSnapshot = { index: null, cities: [], features: [], prices: new Map(), viewport: null, loading: false, detail: false, error: null };
  private listeners = new Set<() => void>();
  private timer: ReturnType<typeof setTimeout> | undefined;
  private generation = 0;
  private disposed = false;
  private authenticated = false;
  private selected: string | null = null;
  private unsubscribe: () => void;
  constructor(private cache: MapDataCache = mapCache) { this.unsubscribe = cache.subscribe(() => this.publish()); }
  subscribe = (callback: () => void) => { this.listeners.add(callback); return () => { this.listeners.delete(callback); }; };
  getSnapshot = () => this.snapshot;
  setAuthenticated(value: boolean) {
    if (value === this.authenticated) return;
    this.authenticated = value;
    if (!value) this.cache.clearPrices();
    if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true);
  }
  async start() {
    // Focus/auth changes and Map/List remounts create a controller over a warm cache.
    // Cache hits do not emit: hydrate before waiting and explicitly publish the result.
    this.publish();
    this.update({ loading: true, error: null });
    try { const index = await this.cache.loadIndex(); this.update({ index, loading: false }); }
    catch { this.update({ loading: false, error: 'Map boundaries are unavailable. Check your connection and retry.' }); }
  }
  move = () => { ++this.generation; clearTimeout(this.timer); };
  idle = (viewport: Viewport, immediate = false) => {
    if (this.disposed) return;
    this.move();
    const generation = this.generation;
    this.update({ viewport, error: null });
    this.publish(); // Cached Imus data appears synchronously, before the debounce/network work.
    this.timer = setTimeout(() => { void this.load(generation); }, immediate ? 0 : 750);
  };
  retry = () => { if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true); else void this.start(); };
  select = (id: string) => {
    this.selected = id;
    const { index, viewport } = this.snapshot;
    // Both polygon and card taps can highlight an already drawn area immediately.
    if (index && viewport && areaBoundaries(mapLevel(index, viewport), index.version)) return;
    this.publish();
    if (this.snapshot.viewport && !this.snapshot.detail) this.idle(this.snapshot.viewport, true);
  };
  private async load(generation: number) {
    if (this.disposed || generation !== this.generation || !this.snapshot.viewport) return;
    this.update({ loading: true });
    try {
      await this.cache.loadIndex();
      if (generation !== this.generation || this.disposed) return;
      const viewport = this.snapshot.viewport;
      const candidates = this.cache.cities(viewport);
      const level = mapLevel(this.cache.index!, viewport);
      const detail = level === 'city' && candidates.length <= MAX_DETAIL_CITIES;
      const areas = visibleAreas(this.cache.index!, viewport);
      const members = areas.flatMap(area => area.members);
      const selected = areas.find(area => area.id === this.selected);
      const outlines = areaBoundaries(level, this.cache.index!.version);
      // A boundary failure must not prevent the independent batch price read (and vice versa).
      const results = await Promise.allSettled([
        outlines ? Promise.resolve() : this.cache.loadBoundaries(detail ? candidates : selected?.members ?? []),
        this.authenticated ? this.cache.loadPrices(members) : Promise.resolve(),
      ]);
      if (generation !== this.generation || this.disposed) return;
      this.publish();
      this.update({ loading: false, error: results.some(result => result.status === 'rejected')
        ? 'Some map data could not load. Cached data is still available. Retry when connected.' : null });
    } catch {
      if (generation === this.generation) this.update({ loading: false, error: 'Map data could not load. Retry when connected.' });
    }
  }
  refresh = () => { if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true); };
  private publish() {
    if (this.disposed) return;
    const viewport = this.snapshot.viewport;
    const candidates = viewport ? this.cache.cities(viewport) : [];
    const cities = viewport ? this.cache.cities(viewport, true) : [];
    const level = viewport && this.cache.index ? mapLevel(this.cache.index, viewport) : 'country';
    const detail = level === 'city' && candidates.length <= MAX_DETAIL_CITIES;
    const areas = viewport && this.cache.index ? visibleAreas(this.cache.index, viewport) : [];
    const members = areas.flatMap(area => area.members);
    const selected = areas.find(area => area.id === this.selected);
    const outlines = this.cache.index && areaBoundaries(level, this.cache.index.version);
    const features = outlines
      ? areas.flatMap(area => outlines.get(area.id) ?? [])
      : (detail ? cities : selected?.members ?? []).flatMap(city => this.cache.feature(city.id) ?? []);
    const previous = this.snapshot.features;
    this.update({ index: this.cache.index, cities, detail,
      features: previous.length === features.length && previous.every((f, i) => f === features[i]) ? previous : features,
      prices: new Map(this.authenticated ? members.flatMap(city => this.cache.price(city.id) ? [[city.id, this.cache.price(city.id)!] as const] : []) : []) });
  }
  private update(patch: Partial<MapSnapshot>) {
    if (this.disposed) return;
    this.snapshot = { ...this.snapshot, ...patch };
    for (const listener of this.listeners) listener();
  }
  dispose() { this.disposed = true; this.move(); this.unsubscribe(); this.listeners.clear(); }
}
