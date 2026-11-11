// Exercise the real advice adapter/hook with controlled HTTP, clock and foreground events.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const effective = '2026-11-09T22:00:00Z';
const fixture = (overrides = {}) => ({
  fuelType: 'gasoline', oilCompany: null, status: 'wait', reason: 'rollback_upcoming', message: 'Exact API explanation.',
  evaluatedAtUtc: '2026-11-09T21:00:00Z', validUntilUtc: effective,
  evidence: [{ confirmationStatus: 'confirmed', effectiveDatePhilippines: '2026-11-10', effectiveAtUtc: effective,
    minChangePerLiter: -2, maxChangePerLiter: -2, oilCompany: null, newsId: '11111111-1111-1111-1111-111111111111',
    adjustmentId: null, sourceUrls: ['https://example.com/fixture'], validUntilUtc: effective, isEffectiveTimeDefaulted: true }],
  forecast: null, ...overrides,
});
function harness() {
  const cells = [], effects = [], timers = new Map(), calls = [], modules = new Map();
  const foreground = new Set();
  let cursor = 0, responder, hook, args = ['gasoline', null], value, now = Date.parse('2026-11-09T21:00:00Z');
  const same = (a, b) => a && b && a.length === b.length && a.every((v, i) => Object.is(v, b[i]));
  const react = {
    useSyncExternalStore(subscribe, getSnapshot) { react.useEffect(() => subscribe(() => {}), [subscribe]); return getSnapshot(); },
    useState(initial) { const i = cursor++; cells[i] ??= { value: typeof initial === 'function' ? initial() : initial };
      return [cells[i].value, next => { cells[i].value = typeof next === 'function' ? next(cells[i].value) : next; }]; },
    useRef(initial) { const i = cursor++; cells[i] ??= { value: { current: initial } }; return cells[i].value; },
    useCallback(fn, deps) { const i = cursor++; if (!cells[i] || !same(cells[i].deps, deps)) cells[i] = { value: fn, deps }; return cells[i].value; },
    useEffect(fn, deps) { const i = cursor++; if (!cells[i] || !same(cells[i].deps, deps)) {
      const previous = cells[i]; cells[i] = { deps }; effects.push(() => { previous?.cleanup?.(); cells[i].cleanup = fn(); });
    } },
  };
  class Clock extends Date { static now() { return now; } }
  class ApiError extends Error { constructor(status, code) { super(code); this.status = status; this.code = code; } }
  function load(file) {
    if (modules.has(file)) return modules.get(file);
    const exports = {}; modules.set(file, exports);
    const filename = path.resolve(__dirname, '../src/fuel', file + '.ts');
    const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
    }).outputText;
    vm.runInNewContext(code, { exports, URLSearchParams, Date: Clock,
      setTimeout(fn, delay) { const id = {}; timers.set(id, { fn, delay }); return id; }, clearTimeout: id => timers.delete(id),
      require(name) {
        if (name === 'react') return react;
        if (name === '@/auth/http') return { ApiError, send: async url => { calls.push(url); return responder(url); } };
        if (name === '@/auth/foreground') return { subscribeToForeground(fn) { foreground.add(fn); return () => foreground.delete(fn); } };
        if (name === './advice') return load('advice');
        if (name === './advice-store') return load('advice-store');
        throw new Error('Unexpected import ' + name);
      },
    }, { filename });
    return exports;
  }
  const render = () => { cursor = 0; value = hook(...args); effects.splice(0).forEach(fn => fn()); return value; };
  return { api: load('advice'), store: (...args) => load('advice-store').fuelAdviceStore(...args), calls, timers,
    respond(fn) { responder = fn; }, start() { hook = load('use-fuel-advice').useFuelAdvice; return render(); }, render,
    select(...next) { args = next; return render(); }, foreground() { foreground.forEach(fn => fn()); },
    advance(ms) { now += ms; }, fireTimer(delay) { const [id, timer] = [...timers.entries()].find(([, timer]) => delay === undefined || timer.delay === delay) ?? []; assert(timer, `Timer exists: ${delay}`); timers.delete(id); timer.fn(); },
    unmount() { cells.forEach(cell => cell?.cleanup?.()); cells.length = 0; },
  };
}
const settle = async () => { for (let i = 0; i < 30; i++) await Promise.resolve(); };
(async () => {
  const h = harness(), { api } = h;
  h.respond(() => fixture());
  await api.getFuelAdvice('gasoline', null);
  assert.equal(h.calls[0], '/fuel-advice?fuelType=gasoline');
  h.respond(() => fixture({ oilCompany: 'Fixture Company' }));
  await api.getFuelAdvice('gasoline', 'Fixture Company');
  assert.equal(h.calls[1], '/fuel-advice?fuelType=gasoline&oilCompany=Fixture+Company');
  for (const invalid of [null, fixture({ status: 'bad' }), fixture({ fuelType: 'diesel' }), fixture({ validUntilUtc: 'bad' })]) {
    h.respond(() => invalid);
    await assert.rejects(() => api.getFuelAdvice('gasoline', null), e => e.code === 'invalid_api_response');
  }
  assert.equal(api.adviceImpact(fixture()).total, 80);
  assert.equal(api.adviceImpact(fixture({ status: 'unknown' })), null);
  assert.equal(api.adviceImpact(fixture({ evidence: [{ ...fixture().evidence[0], minChangePerLiter: -3 }] })), null);
  assert.match(api.adviceHeadline(fixture({ status: 'gas_now', reason: 'increase_upcoming' })), /before the increase/);
  assert.match(api.adviceHeadline(fixture({ status: 'gas_now', reason: 'rollback_effective' })), /Good time/);
  assert.match(api.adviceHeadline(fixture({ status: 'unknown', reason: 'forecast_only' })), /Awaiting/);
  assert.equal(api.adviceIsCurrent(fixture(), Date.parse(effective)), false);
  assert.match(api.adviceTime(fixture().evidence[0]), /6:00.*PHT/);
  const decrease = api.adviceSummary(fixture(), false, false);
  assert.equal(decrease.direction, 'down');
  assert.equal(decrease.action, 'Wait if you can');
  assert.equal(decrease.amount, '₱2.00/L');
  const increase = api.adviceSummary(fixture({ status: 'gas_now', reason: 'increase_upcoming',
    evidence: [{ ...fixture().evidence[0], minChangePerLiter: 1.2, maxChangePerLiter: 1.2 }] }), false, false);
  assert.equal(increase.direction, 'up');
  assert.equal(increase.action, 'Gas up now');
  assert.equal(increase.amount, '₱1.20/L');
  const conflict = api.adviceSummary(fixture({ status: 'unknown', reason: 'conflicting_adjustments' }), false, false);
  assert.equal(conflict.amount, '—');
  assert.equal(conflict.action, 'Check sources');
  const forecast = api.adviceSummary(fixture({ status: 'unknown', reason: 'forecast_only', evidence: [],
    forecast: { ...fixture().evidence[0], confirmationStatus: 'forecast', minChangePerLiter: -2, maxChangePerLiter: -1 } }), false, false);
  assert.equal(forecast.amount, '₱1.00–2.00/L');
  assert.match(forecast.timing, /^Estimated/);
  assert.equal(forecast.action, 'Await confirmation');
  assert.equal(api.adviceSummary(null, false, true).action, 'Temporarily unavailable');
  assert.equal(api.adviceSummary(null, true, false).amount, '—');
  console.log('PASS summary price direction independent of action, forecast ranges, conflicting sources and unavailable states');
  console.log('PASS advice query encoding, response validation, headlines, confirmed impact and Philippine time');

  const flow = harness();
  let finishGasoline;
  flow.respond(url => url.includes('diesel') ? fixture({ fuelType: 'diesel' })
    : new Promise(resolve => { finishGasoline = resolve; }));
  flow.start(); await settle();
  const otherGasoline = flow.store('gasoline', null).subscribe(() => {});
  assert.equal(flow.calls.length, 1); // Header and dashboard share the pending request.
  assert.equal(flow.select('diesel', null).advice, null);
  await settle(); assert.equal(flow.calls.length, 1); // Different fuels share the cooldown queue.
  finishGasoline(fixture()); await settle();
  assert.equal(flow.store('gasoline', null).getSnapshot().result.fuelType, 'gasoline');
  assert.equal(flow.render().advice, null); // A gasoline response cannot become diesel advice.
  flow.advance(1100); flow.fireTimer(1100); await settle();
  assert.equal(flow.render().advice.fuelType, 'diesel');
  assert.equal(flow.calls.length, 2);
  flow.unmount(); otherGasoline(); assert.equal(flow.timers.size, 0);

  const cache = harness(); cache.respond(() => fixture()); cache.start(); await settle();
  const result = cache.render().advice;
  cache.unmount(); cache.respond(() => { throw new Error('Offline'); });
  assert.equal(cache.start().advice, result); await settle();
  cache.foreground(); await settle(); assert.equal(cache.calls.length, 1);
  assert.equal(cache.render().loading, false); // No checking flicker on remount or fresh foreground.
  cache.advance(300000); cache.foreground(); await settle();
  assert.equal(cache.calls.length, 2);
  assert.equal(cache.render().advice, result); // A refresh failure retains unexpired evidence.
  assert.equal(cache.render().error, false);
  assert.equal([...cache.timers.values()][0].delay, 60000);
  cache.unmount(); cache.start(); await settle(); assert.equal(cache.calls.length, 2);
  cache.unmount(); assert.equal(cache.timers.size, 0);

  const slow = harness(); slow.respond(() => fixture()); slow.start(); await settle();
  let failRefresh;
  slow.respond(() => new Promise((resolve, reject) => { failRefresh = reject; }));
  slow.advance(300000); slow.foreground(); await settle();
  assert.equal(slow.render().advice.status, 'wait');
  slow.advance(3300000); slow.fireTimer(3300000);
  assert.equal(slow.render().advice, null); // A hanging refresh cannot extend advice validity.
  failRefresh(new Error('Offline')); await settle();
  assert.equal(slow.render().error, true); slow.unmount();

  const company = harness(); company.respond(() => fixture()); company.start(); await settle();
  company.respond(() => fixture({ oilCompany: 'Fixture Company' }));
  assert.equal(company.select('gasoline', 'Fixture Company').advice, null);
  await settle(); company.advance(1100); company.fireTimer(1100); await settle();
  assert.equal(company.render().advice.oilCompany, 'Fixture Company');
  assert.equal(company.select('gasoline', null).advice.oilCompany, null);
  assert.equal(company.calls.length, 2); company.unmount();

  const expiry = harness(); expiry.advance(3599000);
  expiry.respond(() => fixture()); expiry.start(); await settle(); expiry.render();
  assert.equal([...expiry.timers.values()][0].delay, 1000);
  expiry.advance(1000);
  expiry.respond(() => fixture({ status: 'gas_now', reason: 'rollback_effective', validUntilUtc: '2026-11-16T22:00:00Z' }));
  expiry.fireTimer(); await settle(); assert.equal(expiry.render().advice, null);
  assert.equal(expiry.render().loading, true); // Expired advice is hidden even while queued.
  expiry.advance(100); expiry.fireTimer(100); await settle();
  assert.equal(expiry.render().advice.status, 'gas_now'); expiry.unmount();
  const failure = harness(); failure.respond(() => { throw new Error('Offline'); });
  failure.start(); await settle();
  assert.equal(failure.render().error, true); assert.equal(failure.render().advice, null);
  assert.equal([...failure.timers.values()][0].delay, 60000); failure.unmount();
  const expired = harness(); expired.advance(3600000);
  expired.respond(() => fixture()); expired.start(); await settle();
  assert.equal(expired.render().advice, null);
  assert.equal([...expired.timers.values()][0].delay, 60000);
  expired.unmount(); expired.start(); await settle();
  assert.equal(expired.calls.length, 1); expired.unmount();
  console.log('PASS shared pending requests, paced cross-fuel requests, selection isolation, cached remounts, foreground freshness, failed-refresh retention, validity boundaries and cleanup');
})().catch(error => { console.error(error); process.exitCode = 1; });
