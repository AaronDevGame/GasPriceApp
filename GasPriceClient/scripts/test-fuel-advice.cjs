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
  let cursor = 0, foreground, responder, hook, args = ['gasoline', null], value, now = Date.parse('2026-11-09T21:00:00Z');
  const same = (a, b) => a && b && a.length === b.length && a.every((v, i) => Object.is(v, b[i]));
  const react = {
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
        if (name === '@/auth/foreground') return { subscribeToForeground(fn) { foreground = fn; return () => { foreground = undefined; }; } };
        if (name === './advice') return load('advice');
        throw new Error('Unexpected import ' + name);
      },
    }, { filename });
    return exports;
  }
  const render = () => { cursor = 0; value = hook(...args); effects.splice(0).forEach(fn => fn()); return value; };
  return { api: load('advice'), calls, timers,
    respond(fn) { responder = fn; }, start() { hook = load('use-fuel-advice').useFuelAdvice; return render(); }, render,
    select(...next) { args = next; return render(); }, foreground() { foreground?.(); },
    advance(ms) { now += ms; }, fireTimer() { const [id, timer] = timers.entries().next().value; timers.delete(id); timer.fn(); },
    unmount() { cells.forEach(cell => cell?.cleanup?.()); },
  };
}
const settle = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
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
  console.log('PASS advice query encoding, response validation, headlines, confirmed impact and Philippine time');

  const flow = harness();
  flow.respond(() => fixture()); flow.start(); await settle();
  assert.equal(flow.render().advice.status, 'wait');
  assert.equal([...flow.timers.values()][0].delay, 300000);
  // A slower gasoline response must not replace a newer diesel selection.
  let old;
  flow.respond(() => new Promise(resolve => { old = resolve; }));
  const pending = flow.render().reload();
  flow.respond(() => fixture({ fuelType: 'diesel' }));
  assert.equal(flow.select('diesel', null).advice, null);
  await settle(); assert.equal(flow.render().advice.fuelType, 'diesel');
  old(fixture()); await pending;
  assert.equal(flow.render().advice.fuelType, 'diesel');
  // At the effective time, the old recommendation disappears even while the replacement is loading.
  flow.advance(3600000);
  flow.respond(() => new Promise(() => {})); flow.foreground();
  assert.equal(flow.render().advice, null);
  assert.equal(flow.render().loading, true);
  flow.unmount(); assert.equal(flow.timers.size, 0);

  const expiry = harness(); expiry.advance(3599000);
  expiry.respond(() => fixture()); expiry.start(); await settle(); expiry.render();
  assert.equal([...expiry.timers.values()][0].delay, 1000);
  expiry.advance(1000);
  expiry.respond(() => fixture({ status: 'gas_now', reason: 'rollback_effective', validUntilUtc: '2026-11-16T22:00:00Z' }));
  expiry.fireTimer(); await settle(); assert.equal(expiry.render().advice.status, 'gas_now'); expiry.unmount();
  const failure = harness(); failure.respond(() => { throw new Error('Offline'); });
  failure.start(); await settle();
  assert.equal(failure.render().error, true); assert.equal(failure.render().advice, null);
  assert.equal([...failure.timers.values()][0].delay, 60000); failure.unmount();
  console.log('PASS advice selection races, validity-boundary refresh, foreground refresh, error handling and cleanup');
})().catch(error => { console.error(error); process.exitCode = 1; });
