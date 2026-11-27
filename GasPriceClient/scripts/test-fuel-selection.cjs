const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const root = path.resolve(__dirname, '../src');
const modules = new Map();
class ApiError extends Error { constructor(status) { super(); this.status = status; } }
function compile(name, overrides = {}) {
  if (!Object.keys(overrides).length && modules.has(name)) return modules.get(name);
  const exports = {};
  const filename = path.join(root, name + (fs.existsSync(path.join(root, name + '.tsx')) ? '.tsx' : '.ts'));
  const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: {
    module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX,
  } }).outputText;
  vm.runInNewContext(code, { exports, console, URLSearchParams, setTimeout: (...args) => setTimeout(...args).unref(), clearTimeout,
    requestAnimationFrame: callback => { const frame = {}; queueMicrotask(() => { if (!frame.cancelled) callback(); }); return frame; },
    cancelAnimationFrame: frame => { frame.cancelled = true; },
    require: spec => {
      if (spec in overrides) return overrides[spec];
      const target = spec.startsWith('@/') ? spec.slice(2) : path.posix.join(path.posix.dirname(name), spec);
      return compile(target);
    },
  }, { filename });
  if (!Object.keys(overrides).length) modules.set(name, exports);
  return exports;
}
const selection = compile('fuel/selection');
const index = require('../../GasPriceBackend/Geography/ph-2026q3-v1/index.json');
const manila = { city: 'Manila', province: 'Metro Manila' };
const cebu = { city: 'Cebu City', province: 'Cebu' };
const cavite = { city: '', province: 'Cavite', scope: 'province' };
const cityId = location => selection.mapAreaForLocation(index, location).id;
const cityPrices = {
  cityId: cityId(cebu), status: 'available', priceKind: 'estimated',
  diesel: { minPrice: 52, maxPrice: 58 }, gasoline: { minPrice: 60, maxPrice: 68 },
  gasoline95: { minPrice: 66, maxPrice: 72 }, expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
};
const reportedPrice = { oilCompany: 'Example Oil', fuelGrade: 'RON 91', minPricePerLiter: 64, maxPricePerLiter: 75.08 };
for (const location of [manila, cebu, cavite]) {
  const area = selection.mapAreaForLocation(index, location);
  assert(area, `Map must resolve ${location.city || location.province}`);
  assert(selection.sameLocation(selection.locationForMapArea(area, [manila, cebu, cavite]), location));
}
assert.equal(selection.mapAreaForLocation(index, { city: 'Cebu City', province: 'Cavite' }), null);
assert.equal(selection.mapAreaForLocation(index, { city: '', province: 'Metro Manila' }), null, 'NCR is not an administrative province');
const fake = { ...index, cities: [index.cities.find(c => c.name === 'City of Cebu')] };
fake.cities = [fake.cities[0], { ...fake.cities[0], id: 'duplicate' }];
assert.equal(selection.mapAreaForLocation(fake, cebu), null, 'Ambiguous names must not choose an arbitrary boundary');
console.log('PASS geographic matching: city aliases, Metro Manila, provinces, wrong provinces and ambiguous names');

function harness(options = {}) {
  let cursor = 0, dirty = false, tree, foreground;
  const cells = [], pendingEffects = [], calls = [];
  const prices = new Map();
  const listeners = new Set();
  const mapCache = { index, price: id => prices.get(id), setSession() {},
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); } };
  const auth = { session: { state: 'authenticated' }, loading: false, restore: async () => { calls.push('restore'); } };
  const fuel = { localItem: null, doePrices: null, resolvedArea: null, userCoordinates: null,
    initialLocationPending: options.initialLocationPending ?? false, busy: false, authenticated: true,
    locationNote: 'Use your location', updates: [], refresh: async () => { calls.push('local refresh'); }, useLocation: async () => {} };
  const news = { items: [], reload: async () => { calls.push('news refresh'); } };
  let response = location => Promise.resolve({ locations: [manila, cebu, cavite], selected: {
    ...location, scope: location.city ? 'city' : 'province', weekStart: '2026-11-01', weekEnd: '2026-11-07', prices: [reportedPrice],
  } });
  const equal = (a, b) => a && b && a.length === b.length && a.every((value, i) => Object.is(value, b[i]));
  const memo = (fn, deps) => { const i = cursor++; if (!cells[i] || !equal(cells[i].deps, deps)) cells[i] = { deps, value: fn() }; return cells[i].value; };
  const react = {
    useState: initial => { const i = cursor++; if (!(i in cells)) cells[i] = options.snapshot && initial?.index === null && 'viewport' in initial ? options.snapshot : typeof initial === 'function' ? initial() : initial;
      return [cells[i], value => { const next = typeof value === 'function' ? value(cells[i]) : value; if (!Object.is(next, cells[i])) { cells[i] = next; dirty = true; } }]; },
    useMemo: memo, useImperativeHandle: () => {},
    useRef: current => memo(() => ({ current }), []), useCallback: (fn, deps) => memo(() => fn, deps),
    useEffect: (fn, deps) => { const i = cursor++; if (!cells[i] || !equal(cells[i].deps, deps)) pendingEffects.push(() => { cells[i]?.cleanup?.(); cells[i] = { deps, cleanup: fn() }; }); },
  };
  const api = { getDoePriceBrowse: location => { calls.push(location ?? 'default'); return response(location ?? manila); },
    doeLocationLabel: location => location.city || location.province, doeCoverageLabel: report => report.city || report.province };
  const setComparison = value => { options.comparison = value; dirty = true; };
  const components = new Proxy({}, { get: (_, name) => name });
  const overrides = {
    react, 'expo-router': { useIsFocused: () => true },
    './controller': { MapController: class {
      snapshot = options.snapshot;
      subscribe() { return () => {}; } setActive() { return false; } setAuthenticated() {} resetPriceRequests() {} async start() {}
      isActive() { return true; } dispose() {} select() {}
    } }, './cache': { mapCache }, '@/map/cache': { mapCache }, './map-renderer': { __esModule: true, default: 'MapRenderer' }, '@/auth/foreground': { isAppForeground: () => true, subscribeToAppVisibility: () => () => {} },
    'react/jsx-runtime': { jsx: (type, props) => ({ type, props }), jsxs: (type, props) => ({ type, props }), Fragment: 'Fragment' },
    'react-native': { ...components, View: 'View', Pressable: 'Pressable', ActivityIndicator: 'ActivityIndicator', TextInput: 'TextInput', Platform: { OS: 'web' }, StyleSheet: { create: v => v } },
    'react-native-safe-area-context': { SafeAreaView: 'SafeAreaView' },
    '@/auth/auth-provider': { useAuth: () => auth }, '@/auth/http': { ApiError, errorMessage: () => 'Offline' },
    '@/fuel/api': api, '@/fuel/selection': selection, '@/fuel/use-fuel-prices': { useFuelPrices: () => fuel },
    '@/fuel/header-prices': { useHeaderComparison: () => ({ comparison: options.comparison ?? null, setComparison }) },
    '@/news/use-latest-news': { useLatestNews: () => news }, '@/hooks/use-theme': { useTheme: () => ({}) },
    '@/hooks/use-foreground-refresh': { useForegroundRefresh: fn => { foreground = fn; } },
    '@/map/fuel-map': { __esModule: true, default: 'FuelMap' },
  };
  for (const name of ['mobile-header', 'themed-text', 'refreshable-scroll-view', 'themed-view', 'local-price-board', 'fuel-price-summary', 'dashboard-action', 'fuel-news-headline', 'app-icon', 'app-footer']) overrides['@/components/' + name] = components;
  const Screen = compile(options.snapshot ? 'map/fuel-map' : 'fuel/fuel-prices-screen', overrides).default;
  const render = () => { dirty = false; cursor = 0; tree = Screen(options.props); while (pendingEffects.length) pendingEffects.shift()(); };
  const settle = async () => { for (let i = 0; i < 30; i++) { if (dirty || !tree) render(); await Promise.resolve(); } assert(!dirty, 'Screen must settle'); };
  const nodes = () => { const out = []; const visit = n => { if (Array.isArray(n)) return n.forEach(visit); if (!n || typeof n !== 'object') return; out.push(n); visit(n.props?.children); }; visit(tree); return out; };
  const button = text => nodes().find(n => n.type === 'Pressable' && nodesText(n).includes(text));
  const nodesText = n => !n ? '' : typeof n === 'string' ? n : Array.isArray(n) ? n.map(nodesText).join('') : nodesText(n.props?.children);
  return { settle, nodes, button, calls, fuel, auth, render,
    setMapPrice(id, price) { prices.set(id, price); for (const listener of listeners) listener(); }, foreground: () => foreground(), setResponse: fn => { response = fn; } };
}
(async () => {
  const reload = harness({ initialLocationPending: true });
  const summary = screen => screen.nodes().find(n => ['DoePriceSummary', 'MapPriceSummary', 'LocalPriceBoard'].includes(n.type));
  const areaHeading = screen => screen.nodes().find(n => n.props?.accessibilityRole === 'header').props.children;
  const hasText = (screen, text) => screen.nodes().some(n => n.type === 'ThemedText' && n.props.children === text);
  const map = screen => screen.nodes().find(n => n.type === 'FuelMap').props;
  const browserCalls = screen => screen.calls.filter(call => call === 'default' || typeof call === 'object');
  reload.setMapPrice(cityId(manila), { ...cityPrices, cityId: cityId(manila), priceKind: 'doe_reported' });
  await reload.settle();
  assert.equal(browserCalls(reload).length, 0, 'Startup does not fetch the DOE browse feed');
  assert.equal(areaHeading(reload), 'Local fuel prices');
  assert(hasText(reload, 'Updating local prices…'));
  assert(!hasText(reload, 'Loading DOE prices…'));
  assert.equal(summary(reload), undefined, 'Manila stays hidden while waiting for location');
  reload.fuel.userCoordinates = { latitude: 14.43, longitude: 120.94 };
  reload.fuel.initialLocationPending = false;
  reload.render(); await reload.settle();
  assert.equal(summary(reload), undefined, 'Unresolved coordinates do not fall back to Manila');
  const imusLocation = { city: 'Imus', province: 'Cavite' };
  reload.fuel.resolvedArea = imusLocation;
  reload.fuel.doePrices = { ...imusLocation, prices: [reportedPrice] };
  reload.render(); await reload.settle();
  assert.equal(areaHeading(reload), 'Imus');
  assert.equal(summary(reload).props.report.city, 'Imus', 'Use the report already delivered by the location lookup');
  assert.equal(browserCalls(reload).length, 0);
  reload.fuel.resolvedArea = null;
  reload.fuel.doePrices = null;
  reload.fuel.userCoordinates = null;
  reload.render(); await reload.settle();
  assert.equal(summary(reload).props.prices.cityId, cityId(manila), 'Show cached Manila prices when location access is unavailable');

  const comparison = harness({ comparison: { fuel: 'diesel', location: cebu } }); await comparison.settle();
  const scroll = comparison.nodes().find(n => n.type === 'RefreshableScrollView');
  let comparisonOffset;
  scroll.props.ref.current = { scrollTo: ({ y }) => { comparisonOffset = y; } };
  const [content, companySection] = comparison.nodes().filter(n => n.props?.onLayout);
  content.props.onLayout({ nativeEvent: { layout: { y: 44 } } });
  companySection.props.onLayout({ nativeEvent: { layout: { y: 600 } } });
  await comparison.settle();
  assert.equal(comparisonOffset, 644, 'The header shortcut scrolls to the company section below the map');
  assert.equal(browserCalls(comparison)[0].city, 'Cebu City');
  assert(comparison.button('Sort:'));

  const h = harness();
  h.setMapPrice(cityId(cebu), cityPrices);
  h.setMapPrice(cityId(manila), { ...cityPrices, cityId: cityId(manila) });
  await h.settle();
  assert.equal(map(h).location, null, 'Manila leaves the default map camera intact');
  assert(h.button('View DOE company prices'));
  assert.equal(h.button('Sort:'), undefined);
  map(h).onLocationChange(cebu, cityId(cebu)); await h.settle();
  assert.equal(areaHeading(h), 'Cebu City');
  assert.equal(summary(h).props.prices.gasoline95.maxPrice, 72);
  assert.equal(summary(h).props.prices.priceKind, 'estimated');
  assert.equal(map(h).location, null, 'Tapping a city does not change the camera target');
  assert.equal(browserCalls(h).length, 0);
  assert(!hasText(h, 'Loading DOE prices…'));

  let first, second;
  h.setResponse(location => new Promise(resolve => { if (location.city === 'Cebu City') first = resolve; else second = resolve; }));
  h.button('View DOE company prices').props.onPress(); await h.settle();
  let nodes = h.nodes();
  assert(nodes.findIndex(n => n.type === 'FuelMap') < nodes.findIndex(n => n.props?.children === 'Loading DOE prices…'));
  map(h).onLocationChange(manila, cityId(manila)); await h.settle();
  assert(!hasText(h, 'Loading DOE prices…'), 'Selecting a new city closes the detailed report');
  assert.equal(browserCalls(h).length, 1);
  h.button('View DOE company prices').props.onPress(); await h.settle();
  second({ locations: [cebu, manila], selected: { ...manila, prices: [reportedPrice] } }); await h.settle();
  first({ locations: [cebu, manila], selected: { ...cebu, prices: [reportedPrice] } }); await h.settle();
  assert(hasText(h, 'Manila'), 'A late report cannot replace the new city');
  for (const grade of ['Diesel', 'Gasoline 91', 'Gasoline 95']) assert(hasText(h, grade));
  assert(h.nodes().some(n => n.props?.accessibilityLabel?.startsWith('Example Oil, Gasoline 91, 64.00')));
  map(h).onLocationChange(cebu, cityId(cebu)); await h.settle();
  h.button('View DOE company prices').props.onPress(); await h.settle();
  assert.equal(browserCalls(h).length, 2, 'Reopening a fresh report reuses the previous response');
  assert(!hasText(h, 'Loading DOE prices…'));
  map(h).onLocationChange(cavite); await h.settle();
  h.setResponse(async () => { throw new ApiError(404); });
  h.button('View DOE company prices').props.onPress(); await h.settle();
  assert(hasText(h, 'No recent DOE company prices are available for this area.'));

  h.setResponse(async location => ({ locations: [cebu, manila], selected: { ...location, prices: [reportedPrice] } }));
  h.nodes().find(n => n.props?.accessibilityLabel === 'Choose city or province').props.onPress(); await h.settle();
  assert.equal(browserCalls(h).at(-1), 'default', 'Area search loads its directory on demand');
  h.button('Cebu City').props.onPress(); await h.settle();
  assert.equal(map(h).location.city, 'Cebu City', 'Search selection can center the map');
  const beforeRefresh = browserCalls(h).length;
  h.foreground(); h.foreground(); await h.settle();
  assert.equal(browserCalls(h).length, beforeRefresh, 'Refreshing with details closed skips DOE browsing');
  assert.equal(h.calls.filter(value => value === 'news refresh').length, 1);
  h.fuel.localItem = { area: { city: 'Manila', province: 'Metro Manila', name: 'Manila' } };
  h.render(); await h.settle();
  assert.equal(map(h).location.city, 'Cebu City', 'Late GPS cannot override manual selection');
  map(h).onReturnToLocation(); await h.settle();
  assert.equal(map(h).location.city, 'Manila');
  assert(h.nodes().some(n => n.type === 'RecentPriceUpdates'));
  h.auth.session = null; h.render(); await h.settle();
  assert(h.nodes().some(n => n.props?.title === 'Reconnect'));
  assert.equal(summary(h), undefined);
  let selectedCity;
  const mapProps = { userCoordinates: null, onReturnToLocation: () => { returned++; },
    onLocationChange: (location, id) => { selectedCity = { location, id }; } };
  let returned = 0;
  const mapSnapshot = { index, cities: [], features: [], prices: new Map(), pendingPriceIds: new Set(),
    viewport: null, loading: false, error: null };
  const mapHarness = harness({ props: mapProps, snapshot: mapSnapshot });
  await mapHarness.settle();
  const locationButton = () => mapHarness.nodes().find(n => n.props?.accessibilityLabel === 'Return to my location');
  const metroButton = () => mapHarness.button('Center on Metro Manila');
  const renderer = () => mapHarness.nodes().find(n => n.type === 'MapRenderer').props;
  assert.equal(locationButton(), undefined, 'The device control waits for a usable fix');
  assert.equal(renderer().selected, index.defaultCityId, 'City of Manila is highlighted by default');
  assert.equal(renderer().focusBounds, undefined, 'The highlight leaves the Metro Manila camera intact');
  const hierarchy = compile('map/hierarchy');
  const metroBounds = renderer().metroBounds;
  for (const city of index.cities.filter(city => city.regionId === '1300000000')) {
    assert(city.bbox[0] >= metroBounds[0] && city.bbox[2] <= metroBounds[2]);
    assert(city.bbox[1] >= metroBounds[1] && city.bbox[3] <= metroBounds[3]);
  }
  for (const width of [320, 1200]) {
    const size = { width, height: 560 };
    const camera = hierarchy.defaultMapCamera(index, size, metroBounds);
    assert.equal(hierarchy.mapLevel(index, { ...size, zoom: camera.zoom }), 'city');
    assert(camera.zoom <= compile('map/geometry').fitZoom(metroBounds, width, size.height, 0), 'The city-level camera fits the entire region');
    const initialSnapshot = { ...mapSnapshot, viewport: { ...size, bounds: metroBounds, zoom: camera.zoom } };
    assert.equal(compile('map/labels').placeLabels(initialSnapshot, renderer().selected)[0].id, index.defaultCityId, 'Manila gets the first visible price card');
  }
  mapProps.userCoordinates = { latitude: 14.43, longitude: 120.94 };
  mapHarness.render(); await mapHarness.settle();
  assert(locationButton());
  assert.equal(renderer().selected, null, 'A GPS fix replaces the fallback highlight');
  const camera = () => renderer().locationFocus;
  assert.equal(camera().latitude, 14.43);
  assert.equal(camera().areaBounds, undefined, 'Use GPS until the city is resolved');
  mapProps.userLocation = { city: 'Imus', province: 'Cavite' };
  mapHarness.render(); await mapHarness.settle();
  const imus = index.cities.find(city => /imus/i.test(city.name));
  assert.equal(JSON.stringify(camera().areaBounds), JSON.stringify(imus.bbox), 'Once resolved, center the full Imus outline');
  mapProps.location = cebu;
  mapHarness.render(); await mapHarness.settle();
  assert.equal(JSON.stringify(camera().areaBounds), JSON.stringify(imus.bbox), 'Browsing another city must not change the saved home center');
  const firstFocus = camera();
  mapHarness.render(); await mapHarness.settle();
  assert.equal(camera(), firstFocus, 'Unrelated renders must not recenter a panned map');
  locationButton().props.onPress(); await mapHarness.settle();
  assert.equal(camera().request, 1, 'Recenter works even when the area and coordinates are unchanged');
  locationButton().props.onPress(); await mapHarness.settle();
  assert.equal(camera().request, 2);
  assert.equal(returned, 2);
  mapProps.location = mapProps.userLocation;
  mapHarness.render(); await mapHarness.settle();
  renderer().onSelect(index.defaultCityId); await mapHarness.settle();
  assert.equal(renderer().selected, index.defaultCityId, 'Selecting another city overrides the detected city highlight');
  locationButton().props.onPress(); await mapHarness.settle();
  assert.equal(renderer().selected, imus.id, 'Returning to the same GPS location restores the detected city highlight');
  mapProps.location = cebu;
  mapProps.userCoordinates = { latitude: 51.5, longitude: -0.1 };
  mapHarness.render(); await mapHarness.settle();
  assert.equal(locationButton(), undefined, 'Do not move the Philippines map outside its coverage');
  assert.equal(camera(), null);
  mapProps.userCoordinates = null;
  mapHarness.render(); await mapHarness.settle();
  assert.equal(locationButton(), undefined, 'A cleared location hides the device control');
  const levelButtons = () => mapHarness.nodes().filter(n => n.type === 'Pressable' && n.props?.accessibilityLabel?.startsWith('Show '));
  assert.equal(levelButtons().length, 5);
  assert(levelButtons().every(n => n.props.disabled), 'Level controls wait for a usable map viewport');
  const viewport = { bounds: [120.85, 14.3, 121.05, 14.55], width: 1200, height: 560, zoom: 12 };
  mapSnapshot.viewport = viewport;
  mapHarness.render(); await mapHarness.settle();
  const selectedLevels = () => levelButtons().filter(n => n.props.accessibilityState.selected);
  renderer().onSelect(imus.id); await mapHarness.settle();
  assert.equal(renderer().selected, imus.id, 'Tapping a city overrides the default selection');
  assert.equal(selectedCity, undefined, 'A city without cached prices or a known report only changes the map highlight');
  mapHarness.setMapPrice(imus.id, { ...cityPrices, cityId: imus.id });
  renderer().onSelect(imus.id); await mapHarness.settle();
  assert.equal(selectedCity.id, imus.id);
  assert(selection.sameLocation(selectedCity.location, imusLocation), 'A cached map city can be selected before the DOE area directory loads');
  assert.equal(selectedLevels().length, 1);
  assert.equal(selectedLevels()[0].props.accessibilityLabel, 'Show cities', 'Imus city zoom activates Cities');
  for (const [level, label] of [['province', 'provinces'], ['region', 'regions'], ['island', 'island groups'], ['country', 'nationwide'], ['city', 'cities']]) {
    const button = levelButtons().find(n => n.props.accessibilityLabel === `Show ${label}`);
    button.props.onPress(); await mapHarness.settle();
    const command = mapHarness.nodes().find(n => n.type === 'MapRenderer').props.zoomFocus;
    assert.equal(hierarchy.mapLevel(index, { ...viewport, zoom: command.zoom }), level);
    assert.equal(!!command.center, level === 'country', 'Only Nationwide changes the map center');
    // Model the camera's idle report: active states come from actual zoom, not the last tap.
    viewport.zoom = command.zoom;
    mapHarness.render(); await mapHarness.settle();
    assert.equal(selectedLevels().length, 1);
    assert.equal(selectedLevels()[0].props.accessibilityLabel, `Show ${label}`);
  }
  viewport.zoom = hierarchy.zoomForLevel(index, viewport, 'province');
  mapHarness.render(); await mapHarness.settle();
  assert.equal(selectedLevels()[0].props.accessibilityLabel, 'Show provinces', 'Manual zoom-out clears Cities and activates Provinces');
  metroButton().props.onPress(); await mapHarness.settle();
  assert.deepEqual(renderer().zoomFocus, hierarchy.defaultMapCamera(index, viewport, metroBounds), 'Metro Manila reset uses the same camera as startup');
  assert.equal(renderer().selected, index.defaultCityId, 'Metro Manila reset restores the Manila highlight');
  mapProps.userCoordinates = { latitude: 14.43, longitude: 120.94 };
  mapHarness.render(); await mapHarness.settle();
  assert.equal(metroButton(), undefined);
  assert.equal(renderer().zoomFocus, null, 'A successful fix supersedes the Metro Manila command');
  assert.equal(renderer().selected, selection.mapAreaForLocation(index, cebu).id, 'With a valid fix the fallback no longer overrides the chosen city');
  locationButton().props.onPress(); await mapHarness.settle();
  assert.equal(mapHarness.nodes().find(n => n.type === 'MapRenderer').props.zoomFocus, null, 'Returning to location clears the old level command');
  console.log('PASS map levels: five controls, initial disabled state, city/province zoom synchronization, single active level and button camera commands');
  console.log('PASS map location: Metro Manila city default and reset, Imus fix, stable panning, repeat recenter and out-of-coverage handling');
  console.log('PASS Prices screen: cached city ranges, lazy search and company reports, race protection, comparison shortcut, location and refresh');
})().catch(error => { console.error(error); process.exitCode = 1; });
