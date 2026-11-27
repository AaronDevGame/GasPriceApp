import { mapApi } from './api';
import { featureIntersects, intersects } from './geometry';
import type { BoundaryFile, City, CityPrices, Feature, GeographicIndex, PriceFeed, Viewport } from './types';

type Api = typeof mapApi;
const longRetryInterval = 6 * 60 * 60 * 1000;
const refreshCoalesceInterval = 1100;
export class MapRequestFailure extends Error {
  constructor(public retryAt: number) { super('Map data temporarily unavailable'); }
}

type CachedPrice = { item: CityPrices; generated: number };
const validRange = (range: CityPrices['gasoline']) => range === null || (range.currency === 'PHP' && range.unit === 'liter' &&
  Number.isFinite(range.minPrice) && range.minPrice > 0 && Number.isFinite(range.maxPrice) && range.maxPrice >= range.minPrice);

// Session memory caches survive tab changes, selection changes and renderer remounts.
// Boundaries are immutable per version; prices are isolated by version/PSGC/RON 91.
export class MapDataCache {
  index: GeographicIndex | null = null;
  private indexUntil = 0;
  private indexFlight: Promise<GeographicIndex> | null = null;
  private files = new Map<string, BoundaryFile>();
  private features = new Map<string, Feature>();
  private boundaryFlights = new Map<string, Promise<void>>();
  private priceFlights = new Map<string, Promise<void>>();
  private prices = new Map<string, CachedPrice>();
  private failures = new Map<string, { attempts: number; error: MapRequestFailure }>();
  private refreshedAt = new Map<string, number>();
  private listeners = new Set<() => void>();
  private epoch = 0;
  private session: string | null | undefined;
  private activeDownloads = 0;
  private waiters: (() => void)[] = [];
  constructor(private api: Api = mapApi, private now: () => number = Date.now) {}
  subscribe(listener: () => void) { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; }
  private emit() { for (const listener of this.listeners) listener(); }
  private key(id: string, version = this.index?.version) { return `${version}:${id}:RON91`; }
  setSession(identity: string | null) {
    if (identity !== this.session) { this.session = identity; this.clearPrices(); }
  }
  clearPrices() {
    this.epoch++; this.prices.clear(); this.priceFlights.clear(); this.refreshedAt.clear();
    for (const key of this.failures.keys()) if (key.startsWith('price:')) this.failures.delete(key);
    this.emit();
  }
  private checkRetry(key: string) {
    const failure = this.failures.get(key);
    if (failure && failure.error.retryAt > this.now()) throw failure.error;
  }
  private failed(key: string) {
    const attempts = (this.failures.get(key)?.attempts ?? 0) + 1;
    // Three short retries, then at most one automatic attempt every six hours.
    const error = new MapRequestFailure(this.now() + (attempts <= 3 ? 60000 * 2 ** (attempts - 1) : longRetryInterval));
    this.failures.set(key, { attempts, error });
    return error;
  }
  nextPriceExpiry(cities: City[]) {
    return Math.min(Infinity, ...cities.flatMap(city => {
      const price = this.price(city.id);
      return price ? [Date.parse(price.expiresAtUtc)] : [];
    }));
  }
  nextPriceRefreshAt(cities: City[]) {
    return Math.min(Infinity, ...cities.map(city => {
      const key = this.key(city.id), failure = this.failures.get(`price:${key}`);
      const price = this.price(city.id);
      return failure ? failure.error.retryAt : price ? Date.parse(price.expiresAtUtc) : this.now();
    }));
  }
  async loadIndex() {
    if (this.index && this.now() < this.indexUntil) return this.index;
    if (this.indexFlight) return this.indexFlight;
    this.checkRetry('index');
    this.indexFlight = Promise.resolve().then(() => this.api.index()).then(index => {
      if (index.countryCode !== 'PH' || !/^ph-[a-z0-9-]+$/.test(index.version) || !index.cities?.length ||
          !index.cities.some(city => city.id === index.defaultCityId) ||
          index.cities.some(city => !/^\d{10}$/.test(city.id) || city.bbox.length !== 4 || !city.bbox.every(Number.isFinite))) {
        throw new Error('Invalid Philippine index');
      }
      this.failures.delete('index');
      this.index = index; this.indexUntil = this.now() + 300000; this.emit(); return index;
    }).catch(() => { throw this.failed('index'); }).finally(() => { this.indexFlight = null; });
    return this.indexFlight;
  }
  feature(id: string, version = this.index?.version) { return this.features.get(`${version}:${id}`); }
  price(id: string): CityPrices | undefined {
    const cached = this.prices.get(this.key(id));
    return cached && Date.parse(cached.item.expiresAtUtc) > this.now() ? cached.item : undefined;
  }
  cities(viewport: Viewport, exact = false): City[] {
    return this.index?.cities.filter(city => intersects(city.bbox, viewport.bounds) &&
      (!exact || !this.feature(city.id) || featureIntersects(this.feature(city.id)!, viewport.bounds))) ?? [];
  }
  async loadBoundaries(cities: City[]) {
    const version = this.index?.version;
    if (!version) return;
    await Promise.all([...new Set(cities.map(city => city.fileId))].map(async file => {
      const key = `${version}:${file}`;
      if (this.files.has(key)) return Promise.resolve();
      const existing = this.boundaryFlights.get(key);
      if (existing) return existing;
      this.checkRetry(`boundary:${key}`);
      const flight = this.download(version, file).then(() => { this.failures.delete(`boundary:${key}`); })
        .catch(() => { throw this.failed(`boundary:${key}`); }).finally(() => { this.boundaryFlights.delete(key); });
      this.boundaryFlights.set(key, flight);
      return flight;
    }));
  }
  private async download(version: string, file: string) {
    if (this.activeDownloads >= 4) await new Promise<void>(resolve => this.waiters.push(resolve));
    else this.activeDownloads++;
    try {
      const collection = await this.api.boundary(version, file);
      const members = this.index?.version === version ? this.index.files.find(entry => entry.id === file)?.cityIds : null;
      if (collection.type !== 'FeatureCollection' || !members || collection.features.length !== members.length ||
          new Set(collection.features.map(f => f.id)).size !== members.length ||
          collection.features.some(f => !members.includes(f.id) || !['Polygon', 'MultiPolygon'].includes(f.geometry?.type))) {
        throw new Error('Invalid boundary collection');
      }
      this.files.set(`${version}:${file}`, collection);
      for (const feature of collection.features) this.features.set(`${version}:${feature.id}`, feature);
      this.emit();
    } finally {
      const next = this.waiters.shift();
      if (next) next(); else this.activeDownloads--;
    }
  }
  async loadPrices(cities: City[], refresh = false) {
    const version = this.index?.version;
    if (!version || cities.length === 0) return;
    const waits = new Set<Promise<void>>(), missing: string[] = [];
    let blocked: MapRequestFailure | undefined;
    for (const city of new Map(cities.map(city => [city.id, city])).values()) {
      const key = this.key(city.id, version);
      const flight = this.priceFlights.get(key);
      if (flight) { waits.add(flight); continue; }
      const failure = this.failures.get(`price:${key}`);
      if (failure && failure.error.retryAt > this.now()) {
        if (!blocked || failure.error.retryAt < blocked.retryAt) blocked = failure.error;
        continue;
      }
      const cached = this.price(city.id);
      if (cached && !failure && (!refresh || this.now() - (this.refreshedAt.get(key) ?? -Infinity) < refreshCoalesceInterval)) continue;
      missing.push(city.id);
    }
    if (missing.length) {
      const epoch = this.epoch;
      const requested = new Set(missing);
      // At most the entire Philippine index (1,642 IDs) in one HTTP request.
      const flight = Promise.resolve().then(() => this.api.prices(version, missing)).then(feed => {
        this.validatePrices(feed, version, requested);
        if (epoch !== this.epoch) return; // Logout/account change invalidates older authorized work.
        const generated = Date.parse(feed.generatedAtUtc);
        for (const item of feed.items) {
          const key = this.key(item.cityId, version), previous = this.prices.get(key);
          if (!previous || generated >= previous.generated) {
            this.prices.set(key, { item, generated });
            // A just-completed read also satisfies overlapping focus/foreground events.
            this.refreshedAt.set(key, this.now());
          }
          this.failures.delete(`price:${key}`);
        }
        this.emit();
      }).catch(() => {
        if (epoch === this.epoch) {
          const failures = missing.map(id => this.failed(`price:${this.key(id, version)}`));
          throw failures.reduce((first, failure) => failure.retryAt < first.retryAt ? failure : first);
        }
      }).finally(() => {
        for (const id of missing) if (this.priceFlights.get(this.key(id, version)) === flight) this.priceFlights.delete(this.key(id, version));
      });
      for (const id of missing) this.priceFlights.set(this.key(id, version), flight);
      waits.add(flight);
    }
    await Promise.all(waits);
    if (blocked) throw blocked;
  }
  private validatePrices(feed: PriceFeed, version: string, requested: Set<string>) {
    if (feed.boundaryVersion !== version || feed.gasolineGrade !== 'RON 91' || feed.dieselGrade !== 'DIESEL' ||
        !Number.isFinite(Date.parse(feed.generatedAtUtc)) || feed.items.length !== requested.size ||
        new Set(feed.items.map(item => item.cityId)).size !== requested.size || feed.items.some(item =>
          !requested.has(item.cityId) || item.scope !== 'city' || item.gasolineGrade !== 'RON 91' ||
          !['available', 'partial', 'unavailable'].includes(item.status) ||
          (!Number.isFinite(Date.parse(item.expiresAtUtc)) || Date.parse(item.expiresAtUtc) <= this.now()) || !validRange(item.gasoline) || !validRange(item.diesel) ||
          !validRange(item.gasoline95 ?? null) || !validRange(item.reportedGasoline95 ?? null) ||
          (item.status === 'unavailable' && (item.gasoline !== null || item.diesel !== null || item.gasoline95 != null)))) throw new Error('Invalid city prices');
  }
}
export const mapCache = new MapDataCache();
