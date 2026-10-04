// Actual flow hook and adapters with simulated API and OS boundaries.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const range = value => ({ minPrice: value, maxPrice: value, currency: 'PHP', unit: 'liter' });
const point = (date, value) => ({ dataAsOf: date, prices: { diesel: range(value), gasoline91: range(value), gasoline95: range(value) } });
const feed = { items: [{ ...point('2026-09-28', 60), area: { level: 'province', name: 'Cebu', province: 'Cebu' } }] };
const featured = { groups: ['Luzon', 'Visayas', 'Mindanao'].map(name => ({ name, items: [{ city: name + ' City', province: name, ...point('2026-09-28', 60) }] })) };
function harness(options = {}) {
  const calls = [], effects = [], states = [], modules = new Map();
  let onForeground;
  const auth = { session: { state: options.state ?? 'authenticated' }, loading: false, error: null,
    restore: async () => calls.push('restore'), logout: async () => calls.push('logout') };
  const client = {
    restore: async () => auth.session,
    login: async () => { calls.push('login'); return { state: 'authenticated' }; },
    resume: async () => { calls.push('resume'); return options.resume ? options.resume() : auth.session; },
    request: async (url, init) => {
      calls.push({ url, init });
      if (url === '/fuel-prices/featured') {
        if (options.featuredFailure) throw new Error('Featured feed unavailable');
        return options.featuredFeed ?? featured;
      }
      if (url.startsWith('/fuel-prices/history')) return { items: [point('2026-09-28', 60), point('2026-09-27', 60)] };
      if (url.startsWith('/fuel-prices/doe?')) {
        if (options.doeFailure) throw new Error('DOE feed unavailable');
        return { city: 'Cebu City', province: 'Cebu', weekStart: '2026-09-22', weekEnd: '2026-09-28',
          prices: [{ oilCompany: 'Shell', fuelGrade: 'RON 91', minPricePerLiter: 61, maxPricePerLiter: 63 }] };
      }
      if (url === '/ai/fuel-prices') {
        if (options.aiFailure) throw new Error('AI unavailable');
        return { result: {
          location: { city: 'Cebu City', province: 'Cebu', region: 'Central Visayas', resolved_area: 'Cebu City, Cebu' },
          estimate_area: { level: 'city', name: 'Cebu City' }, data_as_of: '2026-09-28',
          prices: {
            diesel: { min_price: 55, max_price: 59, currency: 'PHP', unit: 'liter' },
            gasoline_91: { min_price: 60, max_price: 64, currency: 'PHP', unit: 'liter' },
            gasoline_95: { min_price: 65, max_price: 69, currency: 'PHP', unit: 'liter' },
          },
        },
          doePrices: options.doeInAi === false ? null : { city: 'Cebu City', province: 'Cebu', weekStart: '2026-09-22', weekEnd: '2026-09-28',
            prices: [{ oilCompany: 'Petron', fuelGrade: 'DIESEL', minPricePerLiter: 99, maxPricePerLiter: 101 }] } };
      }
      return feed;
    },
  };
  function load(relative) {
    if (modules.has(relative)) return modules.get(relative);
    const exports = {}; modules.set(relative, exports);
    const filename = path.resolve(__dirname, '../src', relative + (relative.endsWith('auth-provider') ? '.tsx' : '.ts'));
    const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), { compilerOptions: {
      module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX,
    } }).outputText;
    vm.runInNewContext(code, { exports, URLSearchParams, setTimeout: fn => setTimeout(fn, 0), clearTimeout,
      require: name => {
        if (name === 'react') return {
          createContext: () => ({ Provider: 'AuthProvider' }), useCallback: fn => fn, useEffect: fn => effects.push(fn),
          useRef: current => ({ current }), useState: initial => { const cell = { value: initial }; states.push(cell); return [initial, value => { cell.value = value; }]; },
        };
        if (name === 'react/jsx-runtime') return { jsx: (type, props) => ({ type, props }) };
        if (name === './foreground') return { subscribeToForeground: callback => {
          onForeground = callback; return () => { onForeground = undefined; };
        } };
        if (name === '@/auth/auth-provider') return { useAuth: () => auth };
        if (name === '@/auth/session' || name === './session') return { authClient: client };
        if (name === './location' && relative === 'fuel/use-fuel-prices' && options.locationReason) return {
          requestLocation: async () => { throw new (load('fuel/location-error').LocationError)(options.locationReason); },
        };
        if (name === './http' || name === '@/auth/http') return { ApiError: class extends Error {}, errorMessage: () => 'Connection unavailable' };
        if (name === 'expo-location') return {
          Accuracy: { Balanced: 3 }, requestForegroundPermissionsAsync: async () => { calls.push('permission'); return { granted: !options.denied }; },
          getCurrentPositionAsync: async () => { calls.push('coordinates'); if (options.gpsFailure) throw new Error('GPS unavailable'); return { coords: { latitude: 10.3, longitude: 123.8 } }; },
        };
        if (name.startsWith('@/')) return load(name.slice(2));
        if (name.startsWith('./')) return load(path.posix.join(path.posix.dirname(relative), name));
        throw new Error('Unexpected import: ' + name);
      },
    }, { filename });
    return exports;
  }
  return { load, calls, effects, states, returnToForeground: () => onForeground?.() };
}
async function settle(h) {
  for (let i = 0; i < 50; i++) {
    if (!h.states[6]?.value) return;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  throw new Error('Flow did not settle');
}
(async () => {
  for (const state of ['new', 'signedOut', 'authenticated']) {
    const h = harness({ state }); assert.equal((await h.load('auth/auth-provider').ensureSession()).state, 'authenticated');
    assert.equal(h.calls.includes('login'), state !== 'authenticated');
  }
  const invalid = harness({ state: 'unavailable' }); await assert.rejects(() => invalid.load('auth/auth-provider').ensureSession());
  assert.ok(!invalid.calls.includes('login'));
  console.log('PASS silent startup: new, saved, signed-out and invalid identities');
  let completeResume;
  const provider = harness({ resume: () => new Promise(resolve => { completeResume = resolve; }) });
  provider.load('auth/auth-provider').AuthProvider({ children: null });
  const cleanups = provider.effects.map(effect => effect());
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(provider.states[0].value.state, 'authenticated');
  assert.equal(provider.states[1].value, false);
  provider.returnToForeground();
  provider.returnToForeground();
  assert.equal(provider.calls.filter(call => call === 'resume').length, 1);
  assert.equal(provider.states[1].value, false); // No full-screen auth loading during renewal.
  assert.equal(provider.states[0].value.state, 'authenticated');
  completeResume({ state: 'authenticated' });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(provider.states[1].value, false);
  cleanups.forEach(cleanup => cleanup?.());
  provider.returnToForeground();
  assert.equal(provider.calls.filter(call => call === 'resume').length, 1);
  const offlineProvider = harness({ resume: () => { throw new Error('Offline'); } });
  offlineProvider.load('auth/auth-provider').AuthProvider({ children: null });
  offlineProvider.effects.forEach(effect => effect());
  await new Promise(resolve => setImmediate(resolve));
  offlineProvider.returnToForeground();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(offlineProvider.states[0].value.state, 'authenticated');
  assert.equal(offlineProvider.states[1].value, false);
  assert.equal(offlineProvider.states[2].value, null);
  assert.ok(offlineProvider.states[3].value.at(-1).message.includes('Session check failed'));
  console.log('PASS foreground provider: no blocking loading, duplicate-return coalescing, cleanup and offline screen preservation');
  const granted = harness(); granted.load('fuel/use-fuel-prices').useFuelPrices(); granted.effects[0](); await settle(granted);
  assert.equal(granted.calls[0].url, '/fuel-prices/featured');
  assert.ok(granted.calls.indexOf('permission') > 0);
  const ai = granted.calls.find(call => call.url === '/ai/fuel-prices');
  assert.equal(ai.init.method, 'POST'); assert.equal(ai.init.body, JSON.stringify({ latitude: 10.3, longitude: 123.8 }));
  const local = granted.calls.find(call => call.url?.startsWith('/fuel-prices?'));
  assert.ok(granted.calls.indexOf(ai) < granted.calls.indexOf(local));
  assert.equal(new URLSearchParams(local.url.split('?')[1]).get('city'), 'Cebu City');
  assert.equal(granted.states[3].value.prices[0].oilCompany, 'Petron');
  assert.equal(granted.states[2].value.area.name, 'Cebu City');
  assert.equal(granted.states[2].value.prices.gasoline91.minPrice, 60);
  assert.equal(granted.states[2].value.prices.gasoline91.maxPrice, 64);
  assert.equal(granted.states[0].value.groups.length, 3);
  assert.ok(!granted.calls.some(call => call.url?.startsWith('/fuel-prices/doe?')));
  console.log('PASS featured GET, permission, coordinates POST, then local-area GET');
  const fallback = harness({ doeInAi: false }); fallback.load('fuel/use-fuel-prices').useFuelPrices(); fallback.effects[0](); await settle(fallback);
  assert.equal(fallback.states[3].value.prices[0].oilCompany, 'Shell');
  assert.ok(fallback.calls.some(call => call.url === '/fuel-prices/doe?latitude=10.3&longitude=123.8'));
  const failedDoe = harness({ doeInAi: false, doeFailure: true }); failedDoe.load('fuel/use-fuel-prices').useFuelPrices(); failedDoe.effects[0](); await settle(failedDoe);
  assert.equal(failedDoe.states[2].value.area.name, 'Cebu City');
  assert.equal(failedDoe.states[3].value, null);
  console.log('PASS DOE fallback displays saved rows without blocking local estimates');
  for (const options of [{ denied: true }, { gpsFailure: true }, { aiFailure: true }]) {
    const h = harness(options); h.load('fuel/use-fuel-prices').useFuelPrices(); h.effects[0](); await settle(h);
    assert.equal(h.states[0].value.groups.length, 3);
    assert.equal(h.states[2].value, null);
    if (options.aiFailure) assert.ok(h.states[8].value.includes('location was received'));
    if (options.denied) assert.ok(h.states[8].value.includes('access is blocked'));
    if (!options.aiFailure) assert.ok(!h.calls.some(call => call.url === '/ai/fuel-prices'));
    assert.ok(!h.calls.some(call => call.url?.startsWith('/fuel-prices?')));
  }
  console.log('PASS denial, GPS and AI failures retain featured feed and skip location GET');
  for (const reason of ['timeout', 'unavailable']) {
    const h = harness({ locationReason: reason });
    h.load('fuel/use-fuel-prices').useFuelPrices(); h.effects[0](); await settle(h);
    assert.ok(h.states[8].value.includes(reason === 'timeout' ? 'timed out' : 'could not determine'));
    assert.equal(h.states[6].value, false);
    assert.ok(!h.calls.some(call => call.url === '/ai/fuel-prices'));
  }
  console.log('PASS location timeout and positioning failure show distinct recovery messages');
  const denied = harness({ denied: true }); const screen = denied.load('fuel/use-fuel-prices').useFuelPrices(); denied.effects[0](); await settle(denied);
  const before = denied.calls.length; screen.refresh(); await settle(denied);
  assert.ok(denied.calls.slice(before).some(call => call.url === '/fuel-prices/featured'));
  assert.ok(!denied.calls.slice(before).includes('permission'));
  await screen.signOut(); assert.ok(denied.calls.includes('logout'));
  const cancelled = harness(); cancelled.load('fuel/use-fuel-prices').useFuelPrices(); cancelled.effects[0]()();
  await new Promise(resolve => setTimeout(resolve, 5)); assert.ok(!cancelled.calls.includes('permission'));
  console.log('PASS featured refresh, logout and startup cancellation');
  for (const groups of [
    ['Luzon', 'Visayas', 'Mindanao'].map(name => ({ name, items: [] })),
    [{ name: 'Luzon', items: featured.groups[0].items }, { name: 'Visayas', items: [] }, { name: 'Mindanao', items: [] }],
    featured.groups,
  ]) {
    const h = harness({ denied: true, featuredFeed: { groups } });
    h.load('fuel/use-fuel-prices').useFuelPrices(); h.effects[0](); await settle(h);
    assert.equal(h.states[0].value.groups.reduce((count, group) => count + group.items.length, 0),
      groups.reduce((count, group) => count + group.items.length, 0));
  }
  console.log('PASS empty, partial and full featured responses');
  const api = granted.load('fuel/api');
  const doeRow = (oilCompany, fuelGrade, minPricePerLiter, maxPricePerLiter = minPricePerLiter) =>
    ({ oilCompany, fuelGrade, minPricePerLiter, maxPricePerLiter });
  const cheapest = api.cheapestDoePrices({ prices: [
    doeRow('Fourth', 'RON 91', 65), doeRow('Other fuel', 'DIESEL', 40),
    doeRow('Third', 'RON 91', 63), doeRow('Second', 'RON 91', 61, 64),
    doeRow('First', 'RON 91', 61, 62), doeRow('Invalid', 'RON 91', 0),
  ] }, 'RON 91');
  assert.equal(cheapest.map(row => row.oilCompany).join(','), 'First,Second,Third');
  assert.equal(api.cheapestDoePrices({ prices: [] }, 'RON 91').length, 0);
  assert.equal(api.meaningfulUpdates([point('2026-09-28', 60)]).length, 0);
  assert.equal(api.meaningfulUpdates([point('2026-09-28', 60), point('2026-09-27', 60)]).length, 0);
  const changes = api.meaningfulUpdates(Array.from({ length: 8 }, (_, i) => point(`2026-09-${28-i}`, 60+i)));
  assert.equal(changes.length, 5); assert.equal(changes[0].dataAsOf, '2026-09-28');
  assert.equal(api.formatPrice(range(null)), 'Not available');
  assert.equal(api.formatPrice(granted.states[2].value.prices.gasoline91), '₱60.00–₱64.00');
  console.log('PASS unchanged history, baseline requirement, newest-first five-change limit');
  const code = ts.transpileModule(fs.readFileSync(path.resolve(__dirname, '../src/fuel/location.web.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
  for (const result of ['allowed', 'denied', 'unavailable', 'timeout']) {
    const exports = {};
    vm.runInNewContext(code, { exports, require: () => granted.load('fuel/location-error'), navigator: { geolocation: { getCurrentPosition: (success, failure, options) => {
      assert.equal(options.timeout, 20000);
      if (result === 'allowed') success({ coords: { latitude: 10, longitude: 123 } });
      else failure({ code: result === 'denied' ? 1 : result === 'timeout' ? 3 : 2, PERMISSION_DENIED: 1, TIMEOUT: 3 });
    } } } });
    if (result === 'unavailable' || result === 'timeout') await assert.rejects(() => exports.requestLocation(), error => error.reason === result);
    else assert.equal((await exports.requestLocation())?.latitude ?? null, result === 'allowed' ? 10 : null);
  }
  for (const reason of ['unsupported', 'insecure']) {
    const exports = {};
    vm.runInNewContext(code, { exports, require: () => granted.load('fuel/location-error'),
      isSecureContext: reason !== 'insecure', navigator: reason === 'unsupported' ? {} : { geolocation: {
        getCurrentPosition: () => { throw new Error('Must not request location on insecure origins'); },
      } },
    });
    await assert.rejects(() => exports.requestLocation(), error => error.reason === reason);
  }
  console.log('PASS browser location grant, denial, positioning failure, timeout and unsupported/insecure environments');
})().catch(error => { console.error(error); process.exitCode = 1; });
