// Deterministic checks against real packaged PH boundaries, with simulated transport and time.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const root = path.resolve(__dirname, '..');
const dataRoot = path.resolve(root, '../GasPriceBackend/Geography/ph-2026q3-v1');
const index = JSON.parse(fs.readFileSync(path.join(dataRoot, 'index.json'), 'utf8'));
const imus = index.cities.find(city => /imus/i.test(city.name));
const manila = index.cities.find(city => city.id === index.defaultCityId);
const bacoor = index.cities.find(city => /bacoor/i.test(city.name));
assert(imus && manila && bacoor);
function deferred() { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; }
function harness(overrides = {}) {
  let now = Date.parse('2026-11-10T00:00:00Z'), id = 0;
  const timers = new Map(), calls = { index: 0, files: [], prices: [] };
  class Clock extends Date { static now() { return now; } }
  const feed = ids => ({ boundaryVersion: index.version, gasolineGrade: 'RON 91', dieselGrade: 'DIESEL',
    generatedAtUtc: new Date(now).toISOString(), expiresAtUtc: new Date(now + 300000).toISOString(), items: ids.map(cityId => ({
      cityId, scope: 'city', status: 'available', gasolineGrade: 'RON 91', gasoline: { minPrice: 58.2, maxPrice: 62.4, currency: 'PHP', unit: 'liter' },
      diesel: { minPrice: 50, maxPrice: 55, currency: 'PHP', unit: 'liter' }, expiresAtUtc: new Date(now + 300000).toISOString(),
    })) });
  const api = {
    index: async () => { calls.index++; return structuredClone(index); },
    boundary: async (version, file) => { calls.files.push(file); if (overrides.boundary) return overrides.boundary(version, file); return JSON.parse(fs.readFileSync(path.join(dataRoot, file + '.geojson'), 'utf8')); },
    prices: async (version, ids) => { calls.prices.push([...ids]); return overrides.prices ? overrides.prices(version, ids, feed) : feed(ids); },
  };
  const modules = new Map();
  const load = name => {
    if (modules.has(name)) return modules.get(name);
    const exports = {}; modules.set(name, exports);
    const source = fs.readFileSync(path.join(root, 'src/map', name + '.ts'), 'utf8');
    const code = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 } }).outputText;
    vm.runInNewContext(code, { exports, Date: Clock, Map, Set, Promise, URL, console,
      setTimeout: (fn, delay = 0) => { const key = ++id; timers.set(key, { fn, at: now + delay }); return key; }, clearTimeout: key => timers.delete(key),
      require: spec => spec === './api' ? { mapApi: api } : load(spec.replace('./', '')),
    }, { filename: name });
    return exports;
  };
  const cache = new (load('cache').MapDataCache)(api, () => now);
  const Controller = load('controller').MapController;
  const flush = async () => { for (let i = 0; i < 35; i++) await Promise.resolve(); };
  const advance = async ms => {
    now += ms;
    for (const [key, task] of [...timers]) if (task.at <= now) { timers.delete(key); task.fn(); }
    await flush();
  };
  return { cache, calls, feed, Controller, load, flush, advance };
}
(async () => {
  // No viewport exists before MapRenderer mounts: start must publish a warm index
  // without relying on a network/cache mutation notification to bootstrap the view.
  const warm = harness();
  await warm.cache.loadIndex();
  const warmController = new warm.Controller(warm.cache);
  const warmSnapshots = [];
  warmController.subscribe(() => warmSnapshots.push(warmController.getSnapshot()));
  await warmController.start();
  assert.equal(warm.calls.index, 1, 'Warm startup must reuse the public index');
  assert.equal(warmController.snapshot.index?.countryCode, 'PH', 'Warm startup must mount MapRenderer without a viewport');
  assert.equal(warmController.snapshot.loading, false);
  assert(warmSnapshots.some(snapshot => snapshot.index?.cities.length === 1642));
  warmController.dispose();
  // Mirrors useFocusEffect cleanup/recreation after auth changes or tab refocus,
  // including Strict Mode cleanup before a shared cold request completes.
  for (const authenticated of [false, true, false, true]) {
    const focused = new warm.Controller(warm.cache);
    focused.setAuthenticated(authenticated);
    const observed = [];
    const unsubscribe = focused.subscribe(() => observed.push(focused.getSnapshot()));
    const started = focused.start();
    assert.equal(focused.snapshot.index?.countryCode, 'PH', 'Focus startup hydrates synchronously');
    await started;
    assert.equal(focused.snapshot.index?.cities.length, 1642);
    assert.equal(focused.snapshot.viewport, null, 'Renderer bootstrap must not need a camera event');
    assert.equal(focused.snapshot.loading, false);
    const count = observed.length;
    unsubscribe(); focused.dispose();
    await focused.start();
    assert.equal(observed.length, count, 'Disposed focus controllers cannot notify the screen');
  }
  assert.equal(warm.calls.index, 1, 'Auth/refocus lifecycle must reuse the same index');
  const shared = harness();
  const abandoned = new shared.Controller(shared.cache);
  const replacement = new shared.Controller(shared.cache);
  const abandonedSnapshots = [];
  abandoned.subscribe(() => abandonedSnapshots.push(abandoned.getSnapshot()));
  const abandonedStart = abandoned.start();
  abandoned.dispose();
  const stoppedCount = abandonedSnapshots.length;
  await Promise.all([abandonedStart, replacement.start()]);
  assert.equal(shared.calls.index, 1, 'Concurrent focus controllers share one cold request');
  assert.equal(replacement.snapshot.index?.countryCode, 'PH');
  assert.equal(abandonedSnapshots.length, stoppedCount);
  replacement.dispose();
  console.log('PASS cold/shared/warm index bootstrap, subscription delivery, auth/refocus/remount lifecycle and disposal');
  const h = harness();
  await Promise.all([h.cache.loadIndex(), h.cache.loadIndex()]);
  assert.equal(h.calls.index, 1);
  await Promise.all([h.cache.loadBoundaries([imus]), h.cache.loadBoundaries([imus, bacoor])]);
  assert.equal(h.calls.files.filter(file => file === imus.fileId).length, 1);
  assert(h.cache.feature(imus.id));
  await Promise.all([h.cache.loadPrices([imus]), h.cache.loadPrices([imus])]);
  assert.equal(h.calls.prices.length, 1);
  assert(h.cache.price(imus.id));
  const controller = new h.Controller(h.cache);
  controller.setAuthenticated(true);
  await controller.start();
  const viewport = { bounds: imus.bbox, zoom: 12, width: 900, height: 600 };
  controller.idle(viewport);
  assert(controller.snapshot.features.some(feature => feature.id === imus.id));
  assert(controller.snapshot.prices.has(imus.id), 'Revisiting Imus must synchronously reuse cached prices');
  const before = h.calls.prices.length;
  controller.select(imus.id);
  assert.equal(h.calls.prices.length, before, 'Selection uses the same cache');
  await h.advance(749);
  assert.equal(h.calls.prices.length, before, 'No request before settling');
  controller.move();
  await h.advance(1);
  assert.equal(h.calls.prices.length, before, 'Movement cancels queued viewport work');
  await h.advance(300001);
  assert.equal(h.cache.price(imus.id), undefined, 'Expired prices are not shown');
  controller.idle(viewport, true); await h.advance(0);
  assert(h.calls.prices.length > before && h.cache.price(imus.id));
  controller.dispose();
  console.log('PASS real Imus geometry, shared pan/tap caches, in-flight deduplication, debounce, movement and expiry');

  const gate = deferred();
  const slow = harness({ prices: (_version, ids, feed) => gate.promise.then(() => feed(ids)) });
  const ctl = new slow.Controller(slow.cache); ctl.setAuthenticated(true); await ctl.start();
  ctl.idle({ ...viewport, bounds: imus.bbox }, true); await slow.advance(0);
  ctl.idle({ ...viewport, bounds: manila.bbox }, true); await slow.advance(0);
  gate.resolve(); await slow.flush();
  assert.deepEqual(Array.from(ctl.snapshot.viewport.bounds), manila.bbox);
  assert(!ctl.snapshot.features.some(feature => feature.id === imus.id), 'Old requests cannot replace the current visible polygons');
  assert(!ctl.snapshot.prices.has(imus.id), 'Old response cannot restore the old view');
  ctl.dispose();
  const logoutGate = deferred();
  const logout = harness({ prices: (_version, ids, feed) => logoutGate.promise.then(() => feed(ids)) });
  await logout.cache.loadIndex(); const pending = logout.cache.loadPrices([imus]);
  logout.cache.clearPrices(); logoutGate.resolve(); await pending;
  assert.equal(logout.cache.price(imus.id), undefined, 'Old authorized work cannot repopulate cache after logout');
  console.log('PASS stale viewport completion and logout invalidation');

  const broken = harness({ boundary: async () => { throw new Error('offline'); } });
  const partial = new broken.Controller(broken.cache); partial.setAuthenticated(true); await partial.start();
  partial.idle(viewport, true); await broken.advance(0);
  assert(partial.snapshot.error && partial.snapshot.prices.size > 0, 'Boundary failure must not block batch prices');
  partial.dispose();
  const invalid = harness({ prices: (_version, ids, feed) => ({ ...feed(ids), gasolineGrade: 'RON 95' }) });
  await invalid.cache.loadIndex(); await assert.rejects(invalid.cache.loadPrices([imus]));
  const scoped = harness({ prices: (_version, ids, feed) => { const result = feed(ids); result.items[0].scope = 'province'; return result; } });
  await scoped.cache.loadIndex(); await assert.rejects(scoped.cache.loadPrices([imus]));
  const missing = harness({ prices: (_version, ids, feed) => { const result = feed(ids); result.items.forEach(item => Object.assign(item, { status: 'unavailable', gasoline: null, diesel: null })); return result; } });
  await missing.cache.loadIndex(); await missing.cache.loadPrices([imus]); await missing.cache.loadPrices([imus]);
  assert.equal(missing.calls.prices.length, 1, 'Unavailable results are cached too');
  console.log('PASS independent failure recovery, grade/scope validation and unavailable-result caching');

  const geometry = h.load('geometry');
  const polygon = { type: 'Feature', id: 'fixture', geometry: { type: 'Polygon', coordinates: [
    [[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]], [[4, 4], [4, 6], [6, 6], [6, 4], [4, 4]],
  ] } };
  assert.equal(geometry.featureIntersects(polygon, [4.5, 4.5, 5.5, 5.5]), false, 'Viewport inside a hole is excluded');
  assert.equal(geometry.featureIntersects(polygon, [9, 2, 11, 3]), true);
  assert.equal(geometry.rectangleInside(polygon, [3, 3, 7, 7]), false, 'Labels cannot cover a hole');
  assert.equal(geometry.rectangleInside(polygon, [1, 1, 2, 2]), true);
  assert(geometry.fitZoom(index.bbox, 400, 600) < geometry.fitZoom(imus.bbox, 400, 600));
  const { rangeText, placeLabels } = h.load('labels');
  assert.equal(rangeText(h.feed([imus.id]).items[0].gasoline), '₱58.20–₱62.40/L');
  assert.equal(rangeText(null), '—');
  const feature = { ...polygon, geometry: { type: 'Polygon', coordinates: [polygon.geometry.coordinates[0]] } };
  const city = { ...imus, id: 'a', bbox: [0, 0, 10, 10], labelPoint: [5, 5] };
  const price = h.feed([city.id]).items[0];
  const snap = { detail: true, viewport: { bounds: [0, 0, 10, 10], width: 1000, height: 800, zoom: 12 },
    cities: [city, { ...city, id: 'b' }], features: [{ ...feature, id: 'a' }, { ...feature, id: 'b' }],
    prices: new Map([['a', price], ['b', { ...price, cityId: 'b' }]]) };
  const labels = placeLabels(snap, 'b');
  assert.equal(labels[0].id, 'b', 'Selection has collision priority');
  assert.equal(labels.length, 2, 'Move the second box when another position fits');
  assert(Math.abs(labels[0].x - labels[1].x) >= (labels[0].width + labels[1].width) / 2 + 8 ||
    Math.abs(labels[0].y - labels[1].y) >= geometry.LABEL_HEIGHT + 8);
  const clipped = { bounds: [120.925, 14.36, 120.97, 14.393], width: 480, height: 500, zoom: 14 };
  assert(geometry.project(imus.labelPoint, clipped)[1] < 0, 'Original Imus label is offscreen after panning');
  const imusFeature = h.cache.feature(imus.id);
  const imusSnapshot = { detail: true, viewport: clipped, cities: [imus], features: [imusFeature],
    prices: new Map([[imus.id, h.feed([imus.id]).items[0]]]) };
  const [relocated] = placeLabels(imusSnapshot, null);
  assert(relocated, 'A visible part of Imus still has room for both price ranges');
  const [x, y] = geometry.project(relocated.anchor, clipped);
  const sw = geometry.pointAtPixel([x - relocated.width / 2, y + geometry.LABEL_HEIGHT / 2], clipped);
  const ne = geometry.pointAtPixel([x + relocated.width / 2, y - geometry.LABEL_HEIGHT / 2], clipped);
  assert(geometry.rectangleInside(imusFeature, [...sw, ...ne]));
  assert(x >= relocated.width / 2 + 7.99 && y >= geometry.LABEL_HEIGHT / 2 + 7.99);
  const tight = { ...snap, viewport: { ...snap.viewport, width: 155, height: 100 },
    prices: new Map([['a', { ...price, status: 'unavailable', gasoline: null, diesel: null }], ['b', price]]) };
  const crowded = placeLabels(tight, null);
  assert.deepEqual(Array.from(crowded, label => label.id), ['b'], 'Actual prices take the only available space');

  assert.equal(placeLabels({ ...snap, detail: false }, null).length, 0);
  console.log('PASS rectangle intersections, holes, camera zoom, exact price text and label collision/zoom policies');
})().catch(error => { console.error(error); process.exitCode = 1; });

// App config uses native keys only in the native plugin, never in public extra metadata.
const envNames = ['GOOGLE_MAPS_ANDROID_API_KEY', 'GOOGLE_MAPS_IOS_API_KEY', 'ANDROID_APPLICATION_ID', 'IOS_BUNDLE_IDENTIFIER'];
const savedEnvironment = Object.fromEntries(envNames.map(name => [name, process.env[name]]));
try {
  for (const name of envNames) delete process.env[name];
  const configure = require('../app.config.js');
  const base = JSON.parse(fs.readFileSync(path.join(root, 'app.json'), 'utf8')).expo;
  const empty = configure({ config: base });
  assert.equal(empty.extra.fuelMap.androidConfigured, false);
  assert.equal(empty.extra.fuelMap.iosConfigured, false);
  process.env.GOOGLE_MAPS_ANDROID_API_KEY = 'android-test-fixture';
  process.env.GOOGLE_MAPS_IOS_API_KEY = 'ios-test-fixture';
  process.env.ANDROID_APPLICATION_ID = 'com.example.fixture';
  process.env.IOS_BUNDLE_IDENTIFIER = 'com.example.fixture';
  const configured = configure({ config: base });
  assert.equal(configured.android.package, 'com.example.fixture');
  assert.equal(configured.ios.bundleIdentifier, 'com.example.fixture');
  assert.equal(configured.plugins.at(-1)[1].androidGoogleMapsApiKey, 'android-test-fixture');
  assert.equal(configured.plugins.at(-1)[1].iosGoogleMapsApiKey, 'ios-test-fixture');
  assert.equal(JSON.stringify(configured.extra).includes('test-fixture'), false);
  console.log('PASS native key injection, platform identifiers, missing configuration and public-metadata isolation');
} finally {
  for (const [name, value] of Object.entries(savedEnvironment)) {
    if (value === undefined) delete process.env[name]; else process.env[name] = value;
  }
}
