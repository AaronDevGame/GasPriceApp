import { MapDataCache, MapRequestFailure, mapCache, priceRequestInterval } from './cache';
import { areaBoundaries } from './area-boundaries';
import { MAX_DETAIL_CITIES } from './geometry';
import { mapLevel, visibleAreas } from './hierarchy';
import type { MapSnapshot, Viewport } from './types';

export class MapController {
  snapshot: MapSnapshot = { index: null, cities: [], features: [], prices: new Map(), viewport: null, loading: false, detail: false, error: null };
  private listeners = new Set<() => void>();
  private timer: ReturnType<typeof setTimeout> | undefined;
  private expiryTimer: ReturnType<typeof setTimeout> | undefined;
  private motionTimer: ReturnType<typeof setTimeout> | undefined;
  private motionViewport: Viewport | undefined;
  private motionPriceFlight: Promise<void> | undefined;
  private motionUpdateAt = 0;
  private retryAt = 0;
  private active = true;
  private moving = false;
  private refreshPrices = false;
  private refreshFlight: { generation: number; promise: Promise<void> } | null = null;
  private generation = 0;
  private disposed = false;
  private authenticated = false;
  private selected: string | null = null;
  private unsubscribe: () => void;
  constructor(private cache: MapDataCache = mapCache) {
    this.unsubscribe = cache.subscribe(() => {
      if (!this.active) return;
      this.publish(); this.scheduleRefresh();
    });
  }
  isActive() { return this.active && !this.disposed; }
  setActive(value: boolean) {
    if (value === this.active || this.disposed) return false;
    this.active = value;
    if (!value) { this.cancelViewportWork(); this.update({ loading: false }); }
    else { this.moving = false; this.publish(); this.scheduleRefresh(); }
    return value;
  }
  subscribe = (callback: () => void) => { this.listeners.add(callback); return () => { this.listeners.delete(callback); }; };
  getSnapshot = () => this.snapshot;
  setAuthenticated(value: boolean) {
    if (value === this.authenticated) return;
    this.authenticated = value;
    if (!value) this.cache.clearPrices();
    if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true);
  }
  resetPriceRequests() {
    this.retryAt = 0; this.refreshPrices = false;
    // Invalidate completions from the previous identity before starting its replacement.
    if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true);
  }
  async start() {
    // Cache hits do not emit: hydrate from the warm index before waiting,
    // then explicitly publish the result to bootstrap the renderer.
    if (this.disposed) return;
    this.publish();
    if (!this.active) return;
    this.update({ loading: true, error: null });
    try { const index = await this.cache.loadIndex(); this.retryAt = 0; this.update({ index, loading: false }); }
    catch (error) {
      this.retryAt = this.failureDeadline(error);
      this.update({ loading: false, error: 'Map boundaries are unavailable. Check your connection and retry.' });
    }
    this.scheduleRefresh();
  }
  private cancelViewportWork() {
    ++this.generation;
    clearTimeout(this.timer); this.timer = undefined;
    clearTimeout(this.expiryTimer); this.expiryTimer = undefined;
    clearTimeout(this.motionTimer); this.motionTimer = undefined;
    this.motionViewport = undefined;
    this.motionUpdateAt = 0;
  }
  move = (viewport?: Viewport) => {
    if (!this.active || this.disposed) return;
    if (!this.moving) {
      this.cancelViewportWork();
      this.moving = true;
    }
    if (!viewport || !this.snapshot.viewport) return;
    this.motionViewport = viewport;
    if (this.motionTimer !== undefined || this.motionPriceFlight) return;
    const delay = this.motionUpdateAt - Date.now();
    if (delay > 0) this.motionTimer = setTimeout(this.updateMovingViewport, delay);
    else this.updateMovingViewport();
  };
  private updateMovingViewport = () => {
    this.motionTimer = undefined;
    const viewport = this.motionViewport;
    this.motionViewport = undefined;
    const index = this.cache.index;
    if (!viewport || !index) return;
    this.motionUpdateAt = Date.now() + priceRequestInterval;
    this.publish(viewport);
    if (this.authenticated) {
      const members = visibleAreas(index, viewport).flatMap(area => area.members);
      // The cache records backoff. The settled load reports any failure still relevant to the final view.
      this.motionPriceFlight = this.cache.loadPrices(members).catch(() => {}).finally(() => {
        this.motionPriceFlight = undefined;
        if (this.motionViewport) this.move(this.motionViewport);
      });
    }
  };
  idle = (viewport: Viewport, immediate = false) => {
    if (this.disposed) return;
    this.cancelViewportWork(); this.moving = false;
    const generation = this.generation;
    this.update({ viewport, error: null });
    this.publish(); // Cached Imus data appears synchronously, before the debounce/network work.
    if (!this.active) return;
    this.timer = setTimeout(() => { this.timer = undefined; void this.load(generation); }, immediate ? 0 : 750);
  };
  retry = () => { if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true); else void this.start(); };
  select = (id: string | null) => {
    if (this.selected === id) return;
    this.selected = id;
    const { index, viewport } = this.snapshot;
    // Both polygon and card taps can highlight an already drawn area immediately.
    if (index && viewport && areaBoundaries(mapLevel(index, viewport), index.version)) return;
    this.publish();
    if (this.snapshot.viewport && !this.snapshot.detail) this.idle(this.snapshot.viewport, true);
  };
  private async load(generation: number, refresh = false) {
    if (!this.active || this.disposed || generation !== this.generation || !this.snapshot.viewport) return;
    if (Date.now() < this.retryAt) { this.scheduleRefresh(); return; }
    this.update({ loading: true });
    try {
      await this.cache.loadIndex();
      if (generation !== this.generation || this.disposed || !this.active) return;
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
        this.authenticated ? this.cache.loadPrices(members, refresh || this.refreshPrices) : Promise.resolve(),
      ]);
      if (generation !== this.generation || this.disposed || !this.active) return;
      // A camera/idle event may supersede the generation that requested a forced
      // refresh. Keep that intent until the current view's price read succeeds.
      if (results[1].status === 'fulfilled') this.refreshPrices = false;
      const failures = results.filter(result => result.status === 'rejected');
      this.retryAt = failures.length ? Math.min(...failures.map(result => this.failureDeadline(result.reason))) : 0;
      this.publish();
      this.update({ loading: false, error: failures.length
        ? 'Some map data could not load. Cached data is still available. Retry when connected.' : null });
    } catch (error) {
      if (generation === this.generation && this.active) {
        this.retryAt = this.failureDeadline(error);
        this.update({ loading: false, error: 'Map data could not load. Retry when connected.' });
      }
    } finally { if (generation === this.generation) this.scheduleRefresh(); }
  }
  private failureDeadline(error: unknown) {
    return error instanceof MapRequestFailure ? error.retryAt : Date.now() + 60000;
  }
  private scheduleRefresh() {
    clearTimeout(this.expiryTimer);
    if (!this.active || this.disposed || this.moving || this.timer !== undefined || this.snapshot.loading) return;
    const { index, viewport } = this.snapshot;
    const members = index && viewport ? visibleAreas(index, viewport).flatMap(area => area.members) : [];
    const due = this.retryAt || (this.authenticated && viewport ? this.cache.nextPriceRefreshAt(members) : Infinity);
    // Hide expired prices on time, even if backoff defers the next network attempt.
    const deadline = Math.min(due, this.authenticated ? this.cache.nextPriceExpiry(members) : Infinity);
    if (!Number.isFinite(deadline)) return;
    this.expiryTimer = setTimeout(() => {
      this.publish();
      if (Date.now() < this.retryAt) this.scheduleRefresh();
      else if (this.snapshot.viewport) void this.load(this.generation);
      else void this.start();
    }, Math.max(1, deadline - Date.now()));
  }
  refresh = () => { if (this.snapshot.viewport) this.idle(this.snapshot.viewport, true); };
  // Foreground callers can keep their shared indicator visible until loading settles.
  refreshAndWait = (): Promise<void> => {
    if (!this.active || this.disposed || !this.snapshot.viewport) return Promise.resolve();
    if (this.refreshFlight?.generation === this.generation) return this.refreshFlight.promise;
    this.refreshPrices = true;
    this.cancelViewportWork(); this.moving = false;
    const generation = this.generation;
    const promise = this.load(generation, true).finally(() => {
      if (this.refreshFlight?.promise === promise) this.refreshFlight = null;
    });
    this.refreshFlight = { generation, promise };
    return promise;
  };
  private publish(viewport = this.snapshot.viewport) {
    if (this.disposed) return;
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
    this.update({ index: this.cache.index, viewport, cities, detail,
      features: previous.length === features.length && previous.every((f, i) => f === features[i]) ? previous : features,
      prices: new Map(this.authenticated ? members.flatMap(city => this.cache.price(city.id) ? [[city.id, this.cache.price(city.id)!] as const] : []) : []) });
  }
  private update(patch: Partial<MapSnapshot>) {
    if (this.disposed) return;
    this.snapshot = { ...this.snapshot, ...patch };
    for (const listener of this.listeners) listener();
  }
  dispose() { this.disposed = true; this.cancelViewportWork(); this.unsubscribe(); this.listeners.clear(); }
}
