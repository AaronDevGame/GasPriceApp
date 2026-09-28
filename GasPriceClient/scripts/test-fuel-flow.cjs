// Actual flow hook and adapters with simulated API and OS boundaries.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const range = value => ({ minPrice: value, maxPrice: value, currency: 'PHP', unit: 'liter' });
const point = (date, value) => ({ dataAsOf: date, prices: { diesel: range(value), gasoline91: range(value), gasoline95: range(value) } });
const feed = { items: [{ ...point('2026-09-28', 60), area: { level: 'province', name: 'Cebu', province: 'Cebu' } }] };
function harness(options = {}) {
  const calls = [], effects = [], states = [], modules = new Map();
  const auth = { session: { state: options.state ?? 'authenticated' }, loading: false, error: null,
    restore: async () => calls.push('restore'), logout: async () => calls.push('logout') };
  const client = {
    restore: async () => auth.session,
    login: async () => { calls.push('login'); return { state: 'authenticated' }; },
    request: async (url, init) => {
      calls.push({ url, init });
      if (url.startsWith('/fuel-prices/history')) return { items: [point('2026-09-28', 60), point('2026-09-27', 60)] };
      if (url === '/ai/fuel-prices') {
        if (options.aiFailure) throw new Error('AI unavailable');
        return { result: { location: { city: 'Cebu City', province: 'Cebu', region: 'Central Visayas', resolved_area: 'Cebu City, Cebu' } } };
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
          createContext: () => ({}), useCallback: fn => fn, useEffect: fn => effects.push(fn),
          useRef: current => ({ current }), useState: initial => { const cell = { value: initial }; states.push(cell); return [initial, value => { cell.value = value; }]; },
        };
        if (name === 'react/jsx-runtime') return {};
        if (name === '@/auth/auth-provider') return { useAuth: () => auth };
        if (name === '@/auth/session' || name === './session') return { authClient: client };
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
  return { load, calls, effects, states };
}
async function settle(h) {
  for (let i = 0; i < 50; i++) {
    if (!h.states[3]?.value) return;
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
  const granted = harness(); granted.load('fuel/use-fuel-prices').useFuelPrices(); granted.effects[0](); await settle(granted);
  assert.equal(granted.calls[0].url, '/fuel-prices');
  assert.ok(granted.calls.indexOf('permission') > 0);
  const ai = granted.calls.find(call => call.url === '/ai/fuel-prices');
  assert.equal(ai.init.method, 'POST'); assert.equal(ai.init.body, JSON.stringify({ latitude: 10.3, longitude: 123.8 }));
  const local = granted.calls.find(call => call.url?.startsWith('/fuel-prices?'));
  assert.ok(granted.calls.indexOf(ai) < granted.calls.indexOf(local));
  assert.equal(new URLSearchParams(local.url.split('?')[1]).get('city'), 'Cebu City');
  console.log('PASS initial parameter-free GET, permission, coordinates POST, then resolved-area GET');
  for (const options of [{ denied: true }, { gpsFailure: true }, { aiFailure: true }]) {
    const h = harness(options); h.load('fuel/use-fuel-prices').useFuelPrices(); h.effects[0](); await settle(h);
    assert.equal(h.states[0].value.items.length, 1);
    if (!options.aiFailure) assert.ok(!h.calls.some(call => call.url === '/ai/fuel-prices'));
    assert.ok(!h.calls.some(call => call.url?.startsWith('/fuel-prices?')));
  }
  console.log('PASS denial, GPS and AI failures retain saved feed and skip location GET');
  const manual = harness({ denied: true }); const screen = manual.load('fuel/use-fuel-prices').useFuelPrices(); manual.effects[0](); await settle(manual);
  await screen.selectArea({ province: 'Davao del Sur' }); const before = manual.calls.length; screen.refresh(); await settle(manual);
  assert.ok(manual.calls.slice(before).some(call => call.url === '/fuel-prices?province=Davao+del+Sur'));
  await screen.signOut(); assert.ok(manual.calls.includes('logout'));
  const cancelled = harness(); cancelled.load('fuel/use-fuel-prices').useFuelPrices(); cancelled.effects[0]()();
  await new Promise(resolve => setTimeout(resolve, 5)); assert.ok(!cancelled.calls.includes('permission'));
  console.log('PASS manual selection, area refresh, logout and startup cancellation');
  const api = granted.load('fuel/api');
  assert.equal(api.meaningfulUpdates([point('2026-09-28', 60)]).length, 0);
  assert.equal(api.meaningfulUpdates([point('2026-09-28', 60), point('2026-09-27', 60)]).length, 0);
  const changes = api.meaningfulUpdates(Array.from({ length: 8 }, (_, i) => point(`2026-09-${28-i}`, 60+i)));
  assert.equal(changes.length, 5); assert.equal(changes[0].dataAsOf, '2026-09-28');
  assert.equal(api.formatPrice(range(null)), 'Not available');
  console.log('PASS unchanged history, baseline requirement, newest-first five-change limit');
  const code = ts.transpileModule(fs.readFileSync(path.resolve(__dirname, '../src/fuel/location.web.ts'), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS } }).outputText;
  for (const result of ['allowed', 'denied', 'unavailable']) {
    const exports = {};
    vm.runInNewContext(code, { exports, navigator: { geolocation: { getCurrentPosition: (success, failure, options) => {
      assert.equal(options.timeout, 20000);
      if (result === 'allowed') success({ coords: { latitude: 10, longitude: 123 } });
      else failure({ code: result === 'denied' ? 1 : 2, PERMISSION_DENIED: 1 });
    } } } });
    if (result === 'unavailable') await assert.rejects(() => exports.requestLocation());
    else assert.equal((await exports.requestLocation())?.latitude ?? null, result === 'allowed' ? 10 : null);
  }
  console.log('PASS browser location grant, denial and unavailable position');
})().catch(error => { console.error(error); process.exitCode = 1; });
