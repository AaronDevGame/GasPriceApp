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
    generatedAtUtc: new Date(now).toISOString(), expiresAtUtc: new Date(now + 21600000).toISOString(), items: ids.map(cityId => ({
      cityId, scope: 'city', status: 'available', gasolineGrade: 'RON 91', gasoline: { minPrice: 58.2, maxPrice: 62.4, currency: 'PHP', unit: 'liter' },
      diesel: { minPrice: 50, maxPrice: 55, currency: 'PHP', unit: 'liter' }, expiresAtUtc: new Date(now + 21600000).toISOString(),
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
      require: spec => {
        if (spec === './api') return { mapApi: api };
        if (spec.endsWith('.json')) return require(path.resolve(root, 'src/map', spec));
        return load(spec.replace('./', ''));
      },
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
  return { cache, calls, feed, Controller, load, flush, advance, timers, now: () => now };
}
// Exercise the actual map component lifecycle with its real cache/controller and fake camera/React.
function lifecycle(h, initial = {}) {
  const props = { active: true, ...initial }, cells = [], effects = [], visibility = new Set(), controllers = [];
  let cursor = 0, dirty = true, tree, focused = true, visible = true, handle, playerId = 1;
  const equal = (a, b) => a && b && a.length === b.length && a.every((v, i) => Object.is(v, b[i]));
  const memo = (fn, deps) => { const i = cursor++; if (!cells[i] || !equal(cells[i].deps, deps)) cells[i] = { deps, value: fn() }; return cells[i].value; };
  const react = {
    useState(initial) { const i = cursor++; if (!(i in cells)) cells[i] = typeof initial === 'function' ? initial() : initial;
      return [cells[i], next => { const value = typeof next === 'function' ? next(cells[i]) : next;
        if (!Object.is(value, cells[i])) { cells[i] = value; dirty = true; } }]; },
    useRef: value => memo(() => ({ current: value }), []), useMemo: memo,
    useCallback: (fn, deps) => memo(() => fn, deps), useImperativeHandle: (_ref, create) => { handle = create(); },
    useEffect(fn, deps) { const i = cursor++; if (!cells[i] || !equal(cells[i].deps, deps)) effects.push(() => {
      cells[i]?.cleanup?.(); cells[i] = { deps, cleanup: fn() }; }); },
  };
  function compile(file) {
    const exports = {};
    const code = ts.transpileModule(fs.readFileSync(path.join(root, 'src', file), 'utf8'), { compilerOptions: {
      module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX,
    } }).outputText;
    vm.runInNewContext(code, { exports, require: requireModule, Date, Map, Set, Promise }); return exports;
  }
  function requireModule(spec) {
    if (spec === 'react') return react;
    if (spec === 'expo-router') return { useIsFocused: () => focused };
    if (spec === 'react/jsx-runtime') return { jsx: (type, props) => ({ type, props }), jsxs: (type, props) => ({ type, props }) };
    if (spec === 'react-native') return { View: 'View', Pressable: 'Pressable', ActivityIndicator: 'ActivityIndicator', Linking: {}, StyleSheet: { create: v => v } };
    if (spec === '@/auth/auth-provider') return { useAuth: () => ({ session: { state: 'authenticated', playerId } }) };
    if (spec === '@/auth/foreground') return { isAppForeground: () => visible,
      subscribeToAppVisibility: fn => { visibility.add(fn); return () => visibility.delete(fn); } };
    if (spec === '@/components/app-icon') return { AppIcon: 'AppIcon' };
    if (spec === '@/components/themed-text') return { ThemedText: 'Text' };
    if (spec === './controller') return { MapController: class extends h.Controller { constructor() { super(h.cache); controllers.push(this); } } };
    if (spec === './cache') return { mapCache: h.cache };
    if (spec === './map-renderer') return { __esModule: true, default: 'MapRenderer' };
    if (spec === '@/fuel/selection') return compile('fuel/selection.ts');
    return h.load(spec.replace('@/map/', '').replace('./', ''));
  }
  const Component = compile('map/fuel-map.tsx').default;
  const render = () => { cursor = 0; dirty = false; tree = Component(props); while (effects.length) effects.shift()(); };
  async function settle() {
    for (let i = 0; i < 80; i++) { if (dirty) render(); await Promise.resolve(); }
    assert(!dirty, 'Map component should settle without an update loop');
  }
  function nodes() { const all = []; const visit = node => { if (Array.isArray(node)) return node.forEach(visit);
    if (!node || typeof node !== 'object') return; all.push(node); visit(node.props?.children); }; visit(tree); return all; }
  return { props, controllers, settle, nodes,
    camera: () => nodes().find(node => node.type === 'MapRenderer'),
    refresh: () => handle.refresh(),
    focus(value) { focused = value; dirty = true; },
    session(value) { playerId = value; dirty = true; },
    visibility(value) { if (value !== visible) { visible = value; [...visibility].forEach(fn => fn(value)); } },
    change(next) { Object.assign(props, next); dirty = true; },
    dispose() { cells.forEach(cell => cell?.cleanup?.()); },
  };
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
  await h.advance(21600001);
  assert.equal(h.cache.price(imus.id), undefined, 'Expired prices are not shown');
  controller.idle(viewport, true); await h.advance(0);
  assert(h.calls.prices.length > before && h.cache.price(imus.id));
  controller.dispose();
  console.log('PASS real Imus geometry, shared pan/tap caches, in-flight deduplication, debounce, movement and expiry');

  const dragGate = deferred();
  const panning = harness({ prices: (_version, ids, feed) => panning.calls.prices.length === 2 ? dragGate.promise.then(() => feed(ids)) : feed(ids) });
  const panningUI = lifecycle(panning);
  await panningUI.settle();
  const panCamera = panningUI.camera().props;
  const manilaViewport = { ...viewport, bounds: manila.bbox };
  const bacoorViewport = { ...viewport, bounds: bacoor.bbox };
  const panDavao = index.cities.find(city => city.name === 'City of Davao');
  const davaoViewport = { ...viewport, bounds: panDavao.bbox };
  panCamera.onIdle(viewport);
  await panning.advance(750);
  await panning.advance(800);
  const settledFiles = panning.calls.files.length;
  panCamera.onMove(manilaViewport);
  await panning.flush();
  assert.equal(panning.calls.prices.length, 2, 'The first uncached viewport loads before the camera settles');
  assert(panning.calls.prices[1].includes(manila.id));
  assert.equal(panning.calls.files.length, settledFiles, 'Dragging does not start boundary downloads');
  dragGate.resolve();
  await panning.flush();
  assert(panningUI.controllers[0].snapshot.prices.has(manila.id), 'New prices publish before releasing the drag');
  panCamera.onMove(viewport);
  panCamera.onMove(bacoorViewport);
  await panning.advance(799);
  assert.equal(panning.calls.prices.length, 2);
  await panning.advance(1);
  assert.deepEqual(panningUI.controllers[0].snapshot.viewport.bounds, bacoor.bbox, 'The throttle uses the latest viewport');
  assert(panning.calls.prices.slice(2).every(ids => !ids.includes(manila.id)), 'Panning reuses fresh prices');
  const panPriceRequests = panning.calls.prices.length;
  panCamera.onMove(davaoViewport);
  panCamera.onIdle(bacoorViewport);
  await panning.advance(800);
  assert.deepEqual(panningUI.controllers[0].snapshot.viewport.bounds, bacoor.bbox, 'Idle cancels queued drag work');
  assert.equal(panning.calls.prices.length, panPriceRequests, 'The settled viewport reuses prices loaded during the drag');
  assert(panningUI.controllers[0].snapshot.prices.has(bacoor.id));
  panCamera.onMove(bacoorViewport);
  panCamera.onMove(davaoViewport);
  panCamera.onIdle(davaoViewport);
  await panning.advance(750);
  assert(panning.calls.prices[panPriceRequests].includes(panDavao.id), 'Idle loads prices for a viewport skipped during the drag');
  panningUI.dispose();
  console.log('PASS leading drag price fetch, throttled trailing viewport, cached reuse and settled reconciliation');

  const gate = deferred();
  const slow = harness({ prices: (_version, ids, feed) => gate.promise.then(() => feed(ids)) });
  const ctl = new slow.Controller(slow.cache); ctl.setAuthenticated(true); await ctl.start();
  ctl.idle({ ...viewport, bounds: imus.bbox }, true); await slow.advance(0);
  ctl.idle({ ...viewport, bounds: manila.bbox }, true); await slow.advance(0);
  await slow.advance(799); assert.equal(slow.calls.prices.length, 1, 'Distinct requests share the endpoint cooldown');
  await slow.advance(1); assert.equal(slow.calls.prices.length, 2);
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

  // Foreground UI must stay active until its real map work settles.
  const foregroundGate = deferred();
  const foreground = harness({ prices: async (_v, ids, feed) => {
    await foregroundGate.promise; return feed(ids);
  } });
  const returning = new foreground.Controller(foreground.cache);
  returning.setAuthenticated(true); await returning.start();
  await returning.refreshAndWait(); // Returning before the first camera event is harmless.
  returning.idle({ ...viewport, bounds: imus.bbox }, true);
  let refreshFinished = false;
  const foregroundRequest = returning.refreshAndWait().then(() => { refreshFinished = true; });
  await foreground.flush();
  assert.equal(returning.snapshot.loading, true);
  assert.equal(refreshFinished, false, 'Foreground spinner must wait for prices');
  foregroundGate.resolve(); await foregroundRequest;
  assert.equal(returning.snapshot.loading, false);
  assert.equal(refreshFinished, true);
  assert.equal(foreground.calls.prices.length, 1, 'Foreground refresh replaces pending debounce work');
  returning.dispose(); await returning.refreshAndWait();
  console.log('PASS foreground refresh awaits map loading and tolerates missing viewport/disposal');


  const broken = harness({ boundary: async () => { throw new Error('offline'); } });
  const partial = new broken.Controller(broken.cache); partial.setAuthenticated(true); await partial.start();
  partial.idle(viewport, true); await broken.advance(0);
  assert(partial.snapshot.error && partial.snapshot.prices.size > 0, 'Boundary failure must not block batch prices');
  const boundaryAttempts = broken.calls.files.length, priceAttempts = broken.calls.prices.length;
  partial.idle(viewport, true); await broken.advance(0);
  assert.equal(broken.calls.files.length, boundaryAttempts, 'Boundary failures cannot retry on every camera event');
  await broken.advance(60000); assert(broken.calls.files.length > boundaryAttempts);
  assert.equal(broken.calls.prices.length, priceAttempts, 'Boundary retries reuse valid price data');
  partial.dispose();
  const invalid = harness({ prices: (_version, ids, feed) => ({ ...feed(ids), gasolineGrade: 'RON 95' }) });
  await invalid.cache.loadIndex(); await assert.rejects(invalid.cache.loadPrices([imus]));
  const scoped = harness({ prices: (_version, ids, feed) => { const result = feed(ids); result.items[0].scope = 'province'; return result; } });
  await scoped.cache.loadIndex(); await assert.rejects(scoped.cache.loadPrices([imus]));
  const missing = harness({ prices: (_version, ids, feed) => { const result = feed(ids); result.items.forEach(item => Object.assign(item, { status: 'unavailable', gasoline: null, diesel: null })); return result; } });
  await missing.cache.loadIndex(); await missing.cache.loadPrices([imus]); await missing.cache.loadPrices([imus]);
  assert.equal(missing.calls.prices.length, 1, 'Unavailable results are cached too');
  console.log('PASS independent failure recovery, grade/scope validation and unavailable-result caching');

  const sixHours = 21600000;
  const timed = harness();
  const timedController = new timed.Controller(timed.cache); timedController.setAuthenticated(true); await timedController.start();
  timedController.idle(viewport, true); await timed.advance(0);
  let updates = 0; timedController.subscribe(() => { updates++; });
  assert.equal(timed.timers.size, 1, 'Stationary view has one deadline timer');
  const beforeIdle = updates;
  await timed.advance(15000); await timed.advance(285000);
  assert.equal(updates, beforeIdle, 'No 15-second cache checks or five-minute price reads');
  assert.equal(timed.calls.prices.length, 1);
  await timed.advance(sixHours - 300000);
  assert.equal(timed.calls.prices.length, 2, 'Automatic read occurs at the six-hour expiry');
  timedController.dispose(); assert.equal(timed.timers.size, 0);

  const early = harness({ prices: (_v, ids, feed) => {
    const result = feed(ids); result.items.forEach(item => { item.expiresAtUtc = new Date(early.now() + 120000).toISOString(); }); return result;
  } });
  const earlyController = new early.Controller(early.cache); earlyController.setAuthenticated(true); await earlyController.start();
  earlyController.idle(viewport, true); await early.advance(0); await early.advance(119999);
  assert.equal(early.calls.prices.length, 1); await early.advance(1); assert.equal(early.calls.prices.length, 2);
  earlyController.dispose();
  await missing.advance(300000); await missing.cache.loadPrices([imus]); assert.equal(missing.calls.prices.length, 1);
  await missing.advance(sixHours - 300000); await missing.cache.loadPrices([imus]); assert.equal(missing.calls.prices.length, 2);
  console.log('PASS next-expiry scheduling, six-hour available/unavailable caches, earlier deadlines and no idle device polling');

  let fail = false;
  const backoff = harness({ prices: (_v, ids, feed) => { if (fail) throw new Error('offline'); return feed(ids); } });
  const retryController = new backoff.Controller(backoff.cache); retryController.setAuthenticated(true); await retryController.start();
  retryController.idle(viewport, true); await backoff.advance(0); await backoff.advance(2000);
  fail = true; await retryController.refreshAndWait();
  assert(backoff.cache.price(imus.id), 'Failed refresh retains unexpired prices');
  for (const [delay, requests] of [[60000, 3], [120000, 4], [240000, 5]]) {
    retryController.idle(viewport, true); await backoff.advance(0);
    await retryController.refreshAndWait();
    assert.equal(backoff.calls.prices.length, requests - 1, 'Panning/return events cannot bypass failure backoff');
    await backoff.advance(delay - 1); assert.equal(backoff.calls.prices.length, requests - 1);
    await backoff.advance(1); assert.equal(backoff.calls.prices.length, requests);
  }
  await backoff.advance(300000); assert.equal(backoff.calls.prices.length, 5);
  retryController.setActive(false); assert.equal(backoff.timers.size, 0);
  await backoff.advance(sixHours); assert.equal(backoff.calls.prices.length, 5);
  fail = false; retryController.setActive(true); await retryController.refreshAndWait();
  assert.equal(backoff.calls.prices.length, 6); retryController.dispose();
  console.log('PASS bounded 1/2/4-minute then six-hour failure backoff, cached-data retention and suspended retries');

  const live = harness(); const ui = lifecycle(live, { initialLocationPending: true }); await ui.settle();
  const nationwide = { bounds: index.bbox, zoom: live.load('geometry').fitZoom(index.bbox, 1200, 560, 35), width: 1200, height: 560 };
  ui.camera().props.onIdle(nationwide); await live.advance(750); await ui.settle();
  assert.equal(live.calls.prices.length, 1); assert.equal(live.calls.prices[0].length, index.cities.length);
  await live.advance(2000); ui.visibility(false); await ui.settle();
  assert.equal(live.timers.size, 0); ui.visibility(true);
  // The location flow can finish before the async map activation callback resumes.
  ui.change({ initialLocationPending: false }); await ui.settle(); await live.advance(0); await ui.settle();
  ui.change({ userCoordinates: { latitude: 14.43, longitude: 120.94 }, userLocation: { city: 'Imus', province: 'Cavite' }, initialLocationPending: false });
  await ui.settle(); ui.camera().props.onIdle(viewport); await live.advance(750); await ui.settle();
  assert.equal(live.calls.prices.length, 1, 'Nationwide results cover the initial Imus/location-permission camera change');
  assert.equal(ui.controllers.length, 1, 'Location changes do not recreate the map controller');
  await live.advance(2000); ui.visibility(false); await ui.settle();
  await live.advance(sixHours); await ui.settle(); assert.equal(live.calls.prices.length, 1);
  ui.visibility(true); ui.focus(false); ui.focus(true); await ui.settle();
  const manualOverlap = ui.refresh(); await live.flush(); await ui.settle(); await manualOverlap;
  assert.equal(live.calls.prices.length, 2, 'Overlapping foreground/focus/manual events share one refresh');
  assert(live.calls.prices[1].includes(imus.id));
  await live.advance(2000); ui.focus(false); await ui.settle(); assert.equal(live.timers.size, 0);
  await live.advance(300000); assert.equal(live.calls.prices.length, 2);
  ui.focus(true); await ui.settle(); assert.equal(live.calls.prices.length, 3, 'Returning to screen bypasses still-valid prices');
  await live.advance(2000); ui.change({ active: false }); await ui.settle();
  assert.equal(live.timers.size, 0); ui.visibility(false); ui.visibility(true); await ui.settle(); assert.equal(live.calls.prices.length, 3);
  ui.change({ active: true }); await ui.settle(); assert.equal(live.calls.prices.length, 4, 'Returning from Company list forces current map prices');
  ui.dispose(); assert.equal(live.timers.size, 0);
  console.log('PASS real map lifecycle: permission/local flow, background/tab/focus pause, actual forced return reads and event deduplication');

  const cameraRace = harness(); await cameraRace.cache.loadIndex(); await cameraRace.cache.loadPrices(index.cities);
  const racing = new cameraRace.Controller(cameraRace.cache); racing.setAuthenticated(true); await racing.start();
  racing.idle(viewport, true); await cameraRace.advance(0); await cameraRace.advance(2000);
  const forcedBeforeIdle = racing.refreshAndWait();
  racing.idle(viewport, true); // Supersede the refresh before its cached index await completes.
  await cameraRace.advance(0); await forcedBeforeIdle; await cameraRace.flush();
  assert.equal(cameraRace.calls.prices.length, 2, 'Camera idle cannot downgrade a forced return refresh into cache reuse');
  racing.dispose();
  // Refresh the current view but retain other cities for ordinary panning.
  const retain = harness(); await retain.cache.loadIndex(); await retain.cache.loadPrices([imus, manila]);
  await retain.advance(2000); await Promise.all([retain.cache.loadPrices([imus], true), retain.cache.loadPrices([imus], true)]);
  assert.equal(retain.calls.prices.length, 2); assert.deepEqual(retain.calls.prices[1], [imus.id]);
  await retain.cache.loadPrices([manila]); assert.equal(retain.calls.prices.length, 2);
  const oldFeed = retain.feed([imus.id]); oldFeed.generatedAtUtc = '2026-11-09T00:00:00Z'; oldFeed.items[0].gasoline.minPrice = 1;
  // The cache receives an older generated timestamp on a later refresh: it must retain the newer value.
  const original = retain.cache.price(imus.id).gasoline.minPrice;
  const old = harness({ prices: (_v, ids, feed) => old.calls.prices.length > 1 ? oldFeed : feed(ids) });
  await old.cache.loadIndex(); await old.cache.loadPrices([imus]); await old.advance(2000); await old.cache.loadPrices([imus], true);
  assert.equal(old.cache.price(imus.id).gasoline.minPrice, original);
  const staleFeed = harness({ prices: (_v, ids, feed) => {
    const result = feed(ids); result.items.forEach(item => { item.expiresAtUtc = '2026-11-09T00:00:00Z'; }); return result;
  } });
  await staleFeed.cache.loadIndex();
  await assert.rejects(staleFeed.cache.loadPrices([imus]));
  await assert.rejects(staleFeed.cache.loadPrices([imus]));
  assert.equal(staleFeed.calls.prices.length, 1, 'Already-expired server results cannot cause immediate request loops');
  const oldFailure = deferred();
  const identities = harness({ prices: (_v, ids, feed) => identities.calls.prices.length === 1 ? oldFailure.promise : feed(ids) });
  identities.cache.setSession('first'); await identities.cache.loadIndex();
  const firstIdentity = identities.cache.loadPrices([imus]); await identities.flush();
  identities.cache.setSession('second'); const secondIdentity = identities.cache.loadPrices([imus]);
  await identities.flush(); await identities.advance(800); await secondIdentity;
  oldFailure.reject(new Error('abandoned identity')); await firstIdentity;
  await identities.advance(2000); await identities.cache.loadPrices([imus], true);
  assert.equal(identities.calls.prices.length, 3, 'An old-session failure cannot install backoff for the new identity');
  let unavailableIdentity = true;
  const sessionFailure = harness({ prices: (_v, ids, feed) => {
    if (unavailableIdentity) throw new Error('first identity offline'); return feed(ids);
  } });
  const identityUI = lifecycle(sessionFailure); await identityUI.settle();
  identityUI.camera().props.onIdle(viewport); await sessionFailure.advance(750); await identityUI.settle();
  assert.equal(sessionFailure.calls.prices.length, 1);
  unavailableIdentity = false; identityUI.session(2); await identityUI.settle();
  await sessionFailure.advance(0); await sessionFailure.advance(800); await identityUI.settle();
  assert.equal(sessionFailure.calls.prices.length, 2, 'Controller backoff must not carry over to a new identity');
  identityUI.dispose();
  console.log('PASS forced in-flight sharing, unaffected city reuse, stale-response/backoff and session isolation');

  const geometry = h.load('geometry');
  const polygon = { type: 'Feature', id: 'fixture', geometry: { type: 'Polygon', coordinates: [
    [[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]], [[4, 4], [4, 6], [6, 6], [6, 4], [4, 4]],
  ] } };
  assert.equal(geometry.featureIntersects(polygon, [4.5, 4.5, 5.5, 5.5]), false, 'Viewport inside a hole is excluded');
  assert.equal(geometry.featureIntersects(polygon, [9, 2, 11, 3]), true);
  assert.equal(geometry.rectangleInside(polygon, [3, 3, 7, 7]), false, 'Labels cannot cover a hole');
  assert.equal(geometry.rectangleInside(polygon, [1, 1, 2, 2]), true);
  assert(geometry.fitZoom(index.bbox, 400, 600) < geometry.fitZoom(imus.bbox, 400, 600));
  // The overview centers the country and starts closer than its padded fit on all canvases,
  // while allowing users to pan or zoom out beyond that initial framing.
  const navigationBounds = geometry.cameraBounds(index.bbox);
  assert.deepEqual(Array.from(geometry.cameraBounds([110, 5, 130, 25])), [109, 3, 135, 27],
    'Navigation limits west padding to 5% while retaining 25% east and 10% vertical room');
  for (const areaBounds of [imus.bbox, manila.bbox, index.bbox]) {
    const center = geometry.world(geometry.boundsCenter(areaBounds));
    const nw = geometry.world([areaBounds[0], areaBounds[3]]), se = geometry.world([areaBounds[2], areaBounds[1]]);
    assert(Math.abs(center[0] - (nw[0] + se[0]) / 2) < 1e-10, 'Selected areas have equal horizontal margins');
    assert(Math.abs(center[1] - (nw[1] + se[1]) / 2) < 1e-10, 'Selected areas have equal vertical margins');
  }
  const cameraCenter = geometry.world(geometry.cameraCenter(index.bbox));
  const northwest = geometry.world([index.bbox[0], index.bbox[3]]);
  const southeast = geometry.world([index.bbox[2], index.bbox[1]]);
  assert(Math.abs(cameraCenter[0] - (northwest[0] + (southeast[0] - northwest[0]) * 0.6)) < 1e-10,
    'Camera frames east of the bounding-box midpoint so the main islands move left');
  assert(Math.abs(cameraCenter[1] - (northwest[1] + southeast[1]) / 2) < 1e-10);
  for (const [width, height] of [[320, 560], [400, 600], [1200, 560]]) {
    const fittedZoom = geometry.fitZoom(index.bbox, width, height, geometry.OVERVIEW_PADDING);
    const initialZoom = fittedZoom + geometry.OVERVIEW_ZOOM_OFFSET;
    assert(2 ** (initialZoom - fittedZoom) > 1.3, 'Initial overview renders the country at least 30% larger');
    const scale = 256 * 2 ** fittedZoom;
    assert((width - (southeast[0] - northwest[0]) * scale) / 2 >= geometry.OVERVIEW_PADDING - 1e-8);
    assert((height - (southeast[1] - northwest[1]) * scale) / 2 >= geometry.OVERVIEW_PADDING - 1e-8);
    assert(geometry.fitZoom(navigationBounds, width, height, 0) < initialZoom);
    assert.equal(h.load('hierarchy').mapLevel(index, { bounds: index.bbox, width, height, zoom: initialZoom }), 'country');
  }
  assert(navigationBounds[0] < index.bbox[0] && navigationBounds[1] < index.bbox[1]);
  assert(navigationBounds[2] > index.bbox[2] && navigationBounds[3] > index.bbox[3]);
  const { rangeText, placeLabels } = h.load('labels');
  assert.equal(rangeText(h.feed([imus.id]).items[0].gasoline), '₱58.20–₱62.40/L');
  assert.equal(rangeText(null), '—');
  const feature = { ...polygon, geometry: { type: 'Polygon', coordinates: [polygon.geometry.coordinates[0]] } };
  const city = { ...imus, id: 'a', bbox: [0, 0, 10, 10], labelPoint: [5, 5] };
  const price = h.feed([city.id]).items[0];
  const snap = { index: { ...index, bbox: [-180, -80, 180, 80], cities: [city, { ...city, id: 'b' }] }, detail: true, viewport: { bounds: [0, 0, 10, 10], width: 1000, height: 800, zoom: 12 },
    cities: [city, { ...city, id: 'b' }], features: [{ ...feature, id: 'a' }, { ...feature, id: 'b' }],
    prices: new Map([['a', price], ['b', { ...price, cityId: 'b' }]]) };
  const labels = placeLabels(snap, 'b');
  assert.equal(labels[0].id, 'b', 'Selection has collision priority');
  assert.equal(labels.length, 2, 'Move the second box when another position fits');
  assert(Math.abs(labels[0].x - labels[1].x) >= (labels[0].width + labels[1].width) / 2 + 8 ||
    Math.abs(labels[0].y - labels[1].y) >= labels[0].height + 8);
  const clipped = { bounds: [120.925, 14.36, 120.97, 14.393], width: 480, height: 500, zoom: 14 };
  assert(geometry.project(imus.labelPoint, clipped)[1] < 0, 'Original Imus label is offscreen after panning');
  const imusFeature = h.cache.feature(imus.id);
  const imusSnapshot = { index, detail: true, viewport: clipped, cities: [imus], features: [imusFeature],
    prices: new Map([[imus.id, h.feed([imus.id]).items[0]]]) };
  const [relocated] = placeLabels(imusSnapshot, null);
  assert(relocated, 'A visible part of Imus still has room for both price ranges');
  assert.deepEqual(relocated.center, geometry.boundsCenter(imus.bbox));
  assert.notDeepEqual(relocated.center, relocated.anchor, 'Center Imus itself when the card is displaced');
  const [withoutPrices] = placeLabels({ ...imusSnapshot, prices: new Map() }, imus.id);
  assert.deepEqual(withoutPrices.center, relocated.center, 'Cards without reports center the same area');
  assert.equal(withoutPrices.priceLines.length, 0);
  assert.equal(withoutPrices.coverage, 'No recent report');
  assert.equal(withoutPrices.accessibilityLabel, `${withoutPrices.name}, No recent report`);
  assert.equal(withoutPrices.height, relocated.height - 32, 'Omitting both price rows shrinks the card');
  const imusPrices = imusSnapshot.prices.get(imus.id);
  for (const [missing, fuel] of [['gasoline', 'Diesel'], ['diesel', 'RON 91']]) {
    const [partial] = placeLabels({ ...imusSnapshot, prices: new Map([[imus.id, { ...imusPrices, [missing]: null }]]) }, imus.id);
    assert.equal(partial.priceLines.length, 1);
    assert(partial.priceLines[0].startsWith(`${fuel} ₱`));
    assert.equal(partial.height, relocated.height - 16);
  }
  const [x, y] = geometry.project(relocated.anchor, clipped);
  assert(x >= relocated.width / 2 + 7.99 && y >= relocated.height / 2 + 7.99);
  const hierarchy = h.load('hierarchy');
  for (const [width, height] of [[400, 600], [1200, 560]]) {
    const base = geometry.fitZoom(index.bbox, width, height, 35);
    for (const level of ['country', 'island', 'region', 'province', 'city']) {
      const zoom = hierarchy.zoomForLevel(index, { width, height }, level);
      assert.equal(hierarchy.mapLevel(index, { bounds: imus.bbox, width, height, zoom }), level, `Level button must reach ${level} at ${width}px`);
      assert.equal(hierarchy.mapLevel(index, { bounds: imus.bbox, width, height, zoom: Math.round(zoom) }), level, 'Native whole-number zoom must stay within the requested band');
    }
    for (const [offset, expected] of [[0, 'country'], [1, 'island'], [2.5, 'region'], [4, 'province'], [5.5, 'city']]) {
      assert.equal(hierarchy.mapLevel(index, { bounds: index.bbox, width, height, zoom: base + offset }), expected);
    }
  }
  assert.equal(hierarchy.mapAreas(index, 'country').length, 1);
  const islands = hierarchy.mapAreas(index, 'island');
  assert.equal(islands.length, 3);
  assert.equal(islands.reduce((sum, area) => sum + area.members.length, 0), index.cities.length);
  assert.equal(hierarchy.areaIdentity(imus, 'province')[1], 'Cavite');
  assert.equal(hierarchy.areaIdentity(manila, 'province')[0], manila.id, 'Manila has no administrative province');
  assert.equal(hierarchy.mapAreas(index, 'region').length, 18);
  const country = hierarchy.mapAreas(index, 'country')[0];
  const ranges = hierarchy.areaPrices(country, { prices: new Map([
    [imus.id, h.feed([imus.id]).items[0]],
    [manila.id, { ...h.feed([manila.id]).items[0], gasoline: { minPrice: 40, maxPrice: 90, currency: 'PHP', unit: 'liter' }, diesel: null }],
  ]) });
  assert.equal(ranges.gasoline.minPrice, 40);
  assert.equal(ranges.gasoline.maxPrice, 90);
  assert.equal(ranges.diesel.minPrice, 50, 'Missing diesel reports do not contribute zero');
  assert.equal(ranges.reported, 2);
  assert.equal(ranges.complete, false);
  const estimatedPrice = { ...h.feed([imus.id]).items[0], priceKind: 'estimated', asOf: '2026-11-18',
    weekStart: '2026-11-03', weekEnd: '2026-11-09' };
  const estimatedRange = hierarchy.areaPrices({ members: [imus] }, { prices: new Map([[imus.id, estimatedPrice]]) });
  assert.equal(estimatedRange.estimated, 1);
  assert.match(h.load('labels').priceLabel(estimatedRange), /^Estimated · Nov 18$/);
  const outdatedRange = hierarchy.areaPrices({ members: [imus] }, { prices: new Map([[imus.id,
    { ...estimatedPrice, priceKind: 'doe_reported', isOutdated: true }]]) });
  assert.match(h.load('labels').priceLabel(outdatedRange), /^DOE reported.*Outdated$/);
  const mixedRange = hierarchy.areaPrices(country, { prices: new Map([
    [imus.id, estimatedPrice], [manila.id, { ...h.feed([manila.id]).items[0], priceKind: 'doe_reported' }],
  ]) });
  assert.equal(h.load('labels').priceLabel(mixedRange), 'Includes estimates', 'Aggregate ranges must disclose estimates');
  const overview = { ...snap, index, detail: false, features: [], viewport: { bounds: index.bbox, width: 1200, height: 560,
    zoom: geometry.fitZoom(index.bbox, 1200, 560, 35) } };
  const countryLabels = placeLabels(overview, null);
  assert.equal(countryLabels.length, 1, 'National overview shows a single card');
  assert.equal(countryLabels[0].name, 'Philippines');
  const denseCities = Array.from({ length: 30 }, (_, i) => ({ ...city, id: String(i), bbox: [4.99, 4.99, 5.01, 5.01] }));
  const dense = { ...snap, index: { ...snap.index, cities: denseCities }, cities: denseCities,
    prices: new Map(denseCities.map(c => [c.id, { ...price, cityId: c.id }])) };
  const sparse = placeLabels(dense, '29');
  assert.equal(sparse[0].id, '29', 'Selected card retains collision priority');
  assert.equal(sparse.length, 1, 'Colliding city cards are suppressed');
  const fallbackCity = { ...city, name: '', groupName: 'Cavite' };
  const fallback = placeLabels({ ...snap, index: { ...snap.index, cities: [fallbackCity] }, cities: [fallbackCity] }, null);
  assert.equal(fallback[0].name, 'Cavite');
  // Fetch all members of a visible area, including cities outside the viewport.
  const national = harness();
  const nc = new national.Controller(national.cache); nc.setAuthenticated(true); await nc.start();
  nc.idle(overview.viewport, true); await national.advance(0);
  assert.equal(national.calls.prices[0].length, index.cities.length);
  assert.equal(national.calls.files.length, 0, 'Overview does not download unselected city polygons');
  assert.equal(nc.snapshot.features.length, 0, 'The initial national overview has no selected boundary');

  const baguio = index.cities.find(city => city.name === 'City of Baguio');
  const tuba = index.cities.find(city => city.name === 'Tuba' && city.groupName === 'Benguet');
  const cebu = index.cities.find(city => city.groupName === 'Cebu' && city.name === 'City of Naga');
  const davao = index.cities.find(city => city.name === 'City of Davao');
  for (const [level, offset] of [['island', 1], ['region', 2.5], ['province', 4]]) {
    nc.idle({ ...overview.viewport, zoom: overview.viewport.zoom + offset }, true);
    const displayed = nc.snapshot.features;
    const byId = new Map(displayed.map(f => [f.id, f]));
    assert.deepEqual(new Set(byId.keys()), new Set(hierarchy.mapAreas(index, level).map(area => area.id)),
      `Every ${level} must already have a tap target when the zoom level changes`);
    for (const city of index.cities) {
      const [areaId] = hierarchy.areaIdentity(city, level);
      assert(geometry.containsFeature(byId.get(areaId), city.labelPoint), `${areaId} must still contain ${city.name}`);
    }
    for (const sample of [imus, cebu, davao, baguio, tuba]) {
      const hits = displayed.filter(f => geometry.containsFeature(f, sample.labelPoint));
      const expected = hierarchy.areaIdentity(sample, level)[0];
      assert.deepEqual(Array.from(hits, f => f.id), [expected], `Tapping ${sample.name} selects only its ${level}`);
      nc.select(expected);
      assert.equal(placeLabels(nc.snapshot, expected)[0]?.id, expected, 'A selected polygon receives the matching price card');
    }
    assert(!displayed.some(f => geometry.containsFeature(f, [119.1, 15])), 'Offshore water should have no tap target');
    if (level === 'province') {
      const cavite = byId.get(hierarchy.areaIdentity(imus, level)[0]);
      assert(geometry.polygons(cavite.geometry).some(rings =>
        geometry.insidePolygon(imus.labelPoint, rings) && geometry.insidePolygon(bacoor.labelPoint, rings)),
      'Adjacent cities share one province polygon with no internal city outline');
      // Baguio is an independent city enclosed by Benguet. Each has its own selection target.
      nc.idle({ ...overview.viewport, bounds: baguio.bbox, zoom: overview.viewport.zoom + offset });
      assert(nc.snapshot.features.some(f => f.id === baguio.id));
      assert(!nc.snapshot.features.some(f => f.id === cavite.id), 'Panning removes offscreen provinces');
    }
    await national.advance(750);
    assert.equal(national.calls.files.length, 0, `Tapping a ${level} should not require detailed city downloads`);
  }
  nc.idle({ ...viewport, bounds: imus.bbox }, true); await national.advance(0);
  assert(nc.snapshot.features.some(f => f.id === imus.id), 'Zooming into cities restores detailed city tap targets');
  assert(national.calls.files.includes(imus.fileId));
  nc.dispose();
  console.log('PASS land and card selection at island, region and province zooms, independent-city holes and city detail');
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
