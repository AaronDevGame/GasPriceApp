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
  const visibility = new Set();
  let visible = true, focused = true;
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
      setTimeout(fn, delay) { const id = {}; timers.set(id, { fn, delay, at: now + delay }); return id; }, clearTimeout: id => timers.delete(id),
      require(name) {
        if (name === 'react') return react;
        if (name === 'expo-router') return { useIsFocused: () => focused };
        if (name === '@/auth/http') return { ApiError, send: async url => { calls.push(url); return responder(url); } };
        if (name === '@/auth/foreground') return { isAppForeground: () => visible,
          subscribeToAppVisibility(fn) { visibility.add(fn); return () => visibility.delete(fn); } };
        if (name === './advice') return load('advice');
        if (name === './advice-store') return load('advice-store');
        throw new Error('Unexpected import ' + name);
      },
    }, { filename });
    return exports;
  }
  const render = () => { cursor = 0; value = hook(...args); effects.splice(0).forEach(fn => fn()); return value; };
  return { api: load('advice'), store: (...args) => load('advice-store').fuelAdviceStore(...args), calls, timers,
    respond(fn) { responder = fn; }, start(...next) { if (next.length) args = next; hook = load('use-fuel-advice').useFuelAdvice; return render(); }, render,
    select(...next) { args = next; return render(); },
    focus(next) { focused = next; return render(); },
    visibility(next) { if (next !== visible) { visible = next; [...visibility].forEach(fn => fn(next)); } },
    foreground() { this.visibility(false); this.visibility(true); },
    async tick(ms) {
      const target = now + ms;
      for (let count = 0; count < 1000; count++) {
        await settle();
        const [id, timer] = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0] ?? [];
        if (!timer || timer.at > target) { now = target; await settle(); return; }
        now = Math.max(now, timer.at); timers.delete(id); timer.fn();
      }
      throw new Error('Timer loop did not settle');
    },
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
  for (const fuelType of ['gasoline', 'diesel']) {
    const applied = fixture({ fuelType, status: 'gas_now', reason: 'rollback_effective',
      forecast: { ...fixture().evidence[0], confirmationStatus: 'forecast', minChangePerLiter: -3, maxChangePerLiter: -2 } });
    assert.equal(api.adviceHeadline(applied), 'Good time to refuel');
    assert.equal(api.adviceCurrentDecrease(applied), '↓ ₱2.00/L decrease now in effect');
  }
  for (const advice of [fixture(), fixture({ status: 'neutral', reason: 'increase_effective' }),
    fixture({ status: 'unknown', reason: 'forecast_only', evidence: [] }),
    fixture({ status: 'gas_now', reason: 'increase_upcoming' }),
    fixture({ status: 'gas_now', reason: 'rollback_effective', evidence: [{ ...fixture().evidence[0], confirmationStatus: 'forecast' }] }),
    fixture({ status: 'gas_now', reason: 'rollback_effective', evidence: [{ ...fixture().evidence[0], minChangePerLiter: 2, maxChangePerLiter: 2 }] })]) {
    assert.equal(api.adviceCurrentDecrease(advice), null);
  }
  assert.equal(api.adviceHeadline(fixture({ status: 'unknown', reason: 'forecast_only' })), 'Price change not yet confirmed');
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
  assert.equal(forecast.action, 'Price drop not yet confirmed');
  for (const [min, max, label] of [[-2.9, -2.6, 'Price drop not yet confirmed'],
    [1, 2, 'Price increase not yet confirmed'], [-1, 1, 'Price change not yet confirmed'],
    [0, 0, 'Price change not yet confirmed']]) {
    const advice = fixture({ status: 'unknown', reason: 'forecast_only', evidence: [],
      forecast: { ...fixture().evidence[0], confirmationStatus: 'forecast', minChangePerLiter: min, maxChangePerLiter: max } });
    assert.equal(api.adviceSummary(advice, false, false).action, label);
    assert.equal(api.adviceHeadline(advice, false, false), label);
  }
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

  const longAdvice = (overrides = {}) => fixture({ status: 'neutral', reason: 'no_price_change',
    evidence: [], validUntilUtc: '2026-11-16T22:00:00Z', ...overrides });
  const sixHours = 6 * 60 * 60 * 1000;
  const normal = harness(); normal.respond(() => longAdvice()); normal.start(); await settle();
  assert.equal(normal.calls.length, 1);
  await normal.tick(300000); assert.equal(normal.calls.length, 1); // No five-minute polling.
  await normal.tick(sixHours - 300000); assert.equal(normal.calls.length, 2);
  normal.unmount(); assert.equal(normal.timers.size, 0);
  const browserReload = harness(); browserReload.respond(() => longAdvice()); browserReload.start(); await settle();
  assert.equal(browserReload.calls.length, 1); browserReload.unmount(); // Fresh JS/module state on full page reload.
  console.log('PASS initial/browser-reload fetch and six-hour normal automatic refresh');

  const cache = harness(); cache.respond(() => longAdvice()); cache.start(); await settle();
  const result = cache.render().advice;
  cache.unmount(); assert.equal(cache.start().advice, result); await settle();
  assert.equal(cache.calls.length, 1); // Fresh cached remount does not fetch twice.
  await cache.tick(10000);
  cache.respond(() => { throw new Error('Offline'); }); cache.foreground(); await settle();
  assert.equal(cache.calls.length, 2); // Foreground bypasses the six-hour cache.
  assert.equal(cache.render().advice, result); assert.equal(cache.render().error, false);
  assert.equal([...cache.timers.values()][0].delay, 60000);
  cache.unmount(); cache.start(); await settle(); assert.equal(cache.calls.length, 2);
  cache.unmount(); assert.equal(cache.timers.size, 0);

  const hidden = harness(); hidden.respond(() => longAdvice()); hidden.start(); await settle();
  hidden.visibility(false); assert.equal(hidden.timers.size, 0);
  await hidden.tick(7 * 60 * 60 * 1000); assert.equal(hidden.calls.length, 1);
  hidden.visibility(true); await settle(); assert.equal(hidden.calls.length, 2);
  hidden.visibility(true); await settle(); assert.equal(hidden.calls.length, 2); // Duplicate visibility event.
  hidden.focus(false); // Only local expiry invalidation remains; no automatic HTTP.
  await hidden.tick(7 * 60 * 60 * 1000); assert.equal(hidden.calls.length, 2);
  hidden.focus(true); await settle(); assert.equal(hidden.calls.length, 3); hidden.unmount();
  const initialHidden = harness(); initialHidden.visibility(false); initialHidden.respond(() => longAdvice());
  initialHidden.start(); await settle(); assert.equal(initialHidden.calls.length, 0);
  initialHidden.visibility(true); await settle(); assert.equal(initialHidden.calls.length, 1); initialHidden.unmount();
  const inactive = harness(); inactive.respond(() => longAdvice()); inactive.start('gasoline', null, false);
  await settle(); inactive.foreground(); await inactive.tick(sixHours); assert.equal(inactive.calls.length, 0);
  inactive.select('gasoline', null, true); await settle(); assert.equal(inactive.calls.length, 1); inactive.unmount();
  console.log('PASS pause in background/hidden tabs and unfocused/inactive screens; immediate visible/foreground return');

  const lateFocus = harness(); lateFocus.respond(() => longAdvice()); lateFocus.start(); await settle();
  await lateFocus.tick(5000); lateFocus.focus(false); lateFocus.foreground(); await settle();
  assert.equal(lateFocus.calls.length, 1); // A return event must not fetch for an inactive route.
  lateFocus.focus(true); await settle(); assert.equal(lateFocus.calls.length, 2); lateFocus.unmount();

  const overlap = harness(); overlap.respond(() => longAdvice()); overlap.start(); await settle();
  const extra = overlap.store('gasoline', null).subscribe(() => {});
  await overlap.tick(5000);
  let resolveReturn;
  overlap.respond(() => new Promise(resolve => { resolveReturn = resolve; }));
  overlap.foreground(); overlap.focus(false); overlap.focus(true); overlap.foreground(); await settle();
  assert.equal(overlap.calls.length, 2);
  resolveReturn(longAdvice()); await settle(); assert.equal(overlap.calls.length, 2);
  extra(); overlap.unmount();

  const queued = harness(); let resolveFirst;
  queued.respond(() => new Promise(resolve => { resolveFirst = resolve; })); queued.start(); await settle();
  const cancelDiesel = queued.store('diesel', null).subscribe(() => {}); cancelDiesel();
  resolveFirst(longAdvice()); await settle(); assert.equal(queued.calls.length, 1); queued.unmount();
  const pacing = harness(); pacing.respond(url => longAdvice({ fuelType: url.includes('diesel') ? 'diesel' : 'gasoline' }));
  pacing.start(); await settle(); pacing.select('diesel', null); await settle();
  pacing.visibility(false); await pacing.tick(1100); assert.equal(pacing.calls.length, 1);
  pacing.visibility(true); await settle(); assert.equal(pacing.calls.length, 2); pacing.unmount();
  console.log('PASS overlapping foreground/focus/cards deduplicate; queued requests skip inactive/backgrounded consumers');

  const slow = harness(); slow.respond(() => fixture()); slow.start(); await settle();
  let failRefresh;
  slow.respond(() => new Promise((resolve, reject) => { failRefresh = reject; }));
  await slow.tick(300000); slow.foreground(); await settle();
  assert.equal(slow.render().advice.status, 'wait');
  await slow.tick(3300000);
  assert.equal(slow.render().advice, null); // Hanging requests cannot extend validity.
  failRefresh(new Error('Offline')); await settle(); assert.equal(slow.render().error, true); slow.unmount();

  const company = harness(); company.respond(() => longAdvice()); company.start(); await settle();
  company.respond(() => longAdvice({ oilCompany: 'Fixture Company' }));
  assert.equal(company.select('gasoline', 'Fixture Company').advice, null);
  await company.tick(1100);
  assert.equal(company.render().advice.oilCompany, 'Fixture Company');
  assert.equal(company.select('gasoline', null).advice.oilCompany, null);
  assert.equal(company.calls.length, 2); company.unmount();

  for (const effectiveOnly of [false, true]) {
    const expiry = harness(); expiry.advance(3599000);
    expiry.respond(() => fixture(effectiveOnly ? { validUntilUtc: '2026-11-16T22:00:00Z',
      evidence: [{ ...fixture().evidence[0], validUntilUtc: '2026-11-16T22:00:00Z' }] } : {}));
    expiry.start(); await settle(); expiry.render();
    assert.equal([...expiry.timers.values()][0].delay, 1000);
    expiry.respond(() => longAdvice({ status: 'gas_now', reason: 'rollback_effective', evaluatedAtUtc: effective }));
    await expiry.tick(1000);
    assert.equal(expiry.render().advice, null); assert.equal(expiry.render().loading, true);
    await expiry.tick(100); assert.equal(expiry.render().advice.status, 'gas_now'); expiry.unmount();
  }
  const pausedExpiry = harness(); pausedExpiry.respond(() => fixture()); pausedExpiry.start(); await settle();
  pausedExpiry.visibility(false); await pausedExpiry.tick(3600000);
  assert.equal(pausedExpiry.render().advice, null);
  pausedExpiry.respond(() => longAdvice({ evaluatedAtUtc: effective }));
  pausedExpiry.visibility(true); await settle(); assert.ok(pausedExpiry.render().advice); pausedExpiry.unmount();
  const passiveHeader = harness(); passiveHeader.respond(() => fixture()); passiveHeader.start(); await settle();
  let invalidations = 0;
  const detachHeader = passiveHeader.store('gasoline', null).subscribe(() => { invalidations++; }, false);
  passiveHeader.focus(false); const beforeExpiry = invalidations;
  await passiveHeader.tick(3600000);
  assert.equal(passiveHeader.calls.length, 1); assert.equal(passiveHeader.render().advice, null);
  assert.ok(invalidations > beforeExpiry); // Cached header invalidates locally while requests are paused.
  detachHeader(); passiveHeader.unmount();
  console.log('PASS fuel/company isolation, failed-refresh retention, expiry/effective boundaries and passive-header invalidation');

  const failure = harness(); failure.respond(() => { throw new Error('Offline'); }); failure.start(); await settle();
  assert.equal(failure.render().error, true); assert.equal(failure.render().advice, null);
  for (const [delay, calls] of [[60000, 2], [120000, 3], [240000, 4]]) {
    assert.equal([...failure.timers.values()][0].delay, delay);
    await failure.tick(delay - 1); assert.equal(failure.calls.length, calls - 1);
    await failure.tick(1); assert.equal(failure.calls.length, calls);
  }
  assert.equal([...failure.timers.values()][0].delay, sixHours);
  failure.unmount(); failure.start(); await settle(); assert.equal(failure.calls.length, 4); failure.unmount();
  const expired = harness(); expired.advance(3600000); expired.respond(() => fixture()); expired.start(); await settle();
  assert.equal(expired.render().advice, null); assert.equal([...expired.timers.values()][0].delay, 60000);
  expired.unmount(); expired.start(); await settle(); assert.equal(expired.calls.length, 1); expired.unmount();
  console.log('PASS bounded retries at 1/2/4 minutes then six hours, expired-response backoff and cleanup');
})().catch(error => { console.error(error); process.exitCode = 1; });
