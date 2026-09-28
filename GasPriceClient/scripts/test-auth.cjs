// Adapter contracts with simulated HTTP and OS storage; credentials are fixtures.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const { randomUUID } = require('node:crypto');

function harness(platform, storage = new Map(), location, timerScale = 1) {
  const calls = [];
  const timeouts = [];
  let responder;
  let failStorage = false;
  let now = Date.now();
  const appListeners = new Set(), visibilityListeners = new Set();
  const appState = { currentState: 'active', addEventListener: (event, listener) => {
    assert.equal(event, 'change'); appListeners.add(listener);
    return { remove: () => appListeners.delete(listener) };
  } };
  const document = { visibilityState: 'visible',
    addEventListener: (event, listener) => { assert.equal(event, 'visibilitychange'); visibilityListeners.add(listener); },
    removeEventListener: (event, listener) => visibilityListeners.delete(listener),
  };
  class ClockDate extends Date {
    constructor(...values) { super(...(values.length ? values : [now])); }
    static now() { return now; }
  }
  const modules = new Map();
  function load(file) {
    if (modules.has(file)) return modules.get(file);
    const exports = {};
    modules.set(file, exports);
    const filename = path.resolve(__dirname, '../src/auth', file + '.ts');
    const code = ts.transpileModule(fs.readFileSync(filename, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022 },
    }).outputText;
    vm.runInNewContext(code, {
      exports, URL, AbortController, clearTimeout, __DEV__: false, Date: ClockDate, document,
      setTimeout: (callback, delay) => { timeouts.push(delay); return setTimeout(callback, delay * timerScale); },
      ...(location ? { window: { location } } : {}),
      process: { env: { EXPO_PUBLIC_API_URL: 'https://api.example.test' } },
      fetch: async (url, init) => { calls.push({ url, ...init }); return responder(url, init); },
      require: (name) => {
        if (name === 'react-native') return { Platform: { OS: platform }, AppState: appState };
        if (name === 'expo-crypto') return { randomUUID };
        if (name === 'expo-secure-store') {
          assert.notEqual(platform, 'web');
          return {
            WHEN_UNLOCKED_THIS_DEVICE_ONLY: 'device-only',
            getItemAsync: async (key) => storage.get(key) ?? null,
            setItemAsync: async (key, value) => {
              if (failStorage) throw new Error('Simulated storage failure');
              storage.set(key, value);
            },
          };
        }
        if (name.startsWith('./')) return load(name.slice(2));
        throw new Error('Unexpected module');
      },
    }, { filename });
    return exports;
  }
  return {
    client: load(platform === 'web' ? 'session.web' : 'session').authClient, calls, storage,
    http: load('http'), timeouts,
    tokenLog: load('token-log'), foreground: () => load(platform === 'web' ? 'foreground.web' : 'foreground'),
    advance: milliseconds => { now += milliseconds; },
    appState: next => { appState.currentState = next; for (const listener of appListeners) listener(next); },
    visibility: next => { document.visibilityState = next; for (const listener of visibilityListeners) listener(); },
    respond: (fn) => { responder = fn; }, failStorage: (value) => { failStorage = value; },
  };
}
function response(data, status = 200, code = '') {
  return { status, ok: status < 400, headers: new Map(), json: async () => ({ data, error: { error: code } }) };
}
const key = 'gasprice.guest-session.v1';
const identity = { appInstanceId: randomUUID(), accessToken: 'old-access-fixture', guestCredential: 'guest-fixture', signedOut: false };
const player = { playerName: 'Guest fixture', playerId: 123456789012345 };

(async () => {
  const directWeb = harness('web', new Map(), { hostname: 'localhost', port: '8081' });
  await assert.rejects(() => directWeb.client.restore(), error => error.code === 'web_proxy_required');
  assert.equal(directWeb.calls.length, 0);
  const badResponse = harness('web');
  badResponse.respond(() => ({ status: 404, ok: false, headers: new Map(), json: async () => { throw new SyntaxError('HTML page'); } }));
  await assert.rejects(() => badResponse.client.restore(), error => error.code === 'invalid_api_response' && error.status === 404);
  // A syntactically valid page response still needs the shared API envelope.
  badResponse.respond(() => ({ status: 200, ok: true, headers: new Map(), json: async () => ({ unrelated: true }) }));
  await assert.rejects(() => badResponse.client.restore(), error => error.code === 'invalid_api_response');
  badResponse.respond(() => ({ status: 502, ok: false, headers: new Map(), json: async () => { throw new SyntaxError('Proxy error'); } }));
  await assert.rejects(() => badResponse.client.restore(), error => badResponse.http.errorMessage(error).includes('temporarily unavailable'));
  assert.ok(badResponse.http.errorMessage(new badResponse.http.ApiError(0, 'request_timeout')).includes('too long'));
  const timeout = harness('web', new Map(), undefined, 0);
  timeout.respond((url, init) => ({ status: 200, ok: true, headers: new Map(), json: () => new Promise((_, reject) => {
    init.signal.addEventListener('abort', () => reject(new Error('Aborted response body')), { once: true });
  }) }));
  await assert.rejects(() => timeout.client.restore(), error => error.code === 'request_timeout');
  assert.equal(timeout.timeouts[0], 60000);
  console.log('PASS web: direct Metro origin, HTML responses, invalid envelopes, proxy failures and timeout messages');

  const fresh = harness('ios');
  assert.equal((await fresh.client.restore()).state, 'new');
  assert.equal(fresh.calls.length, 0);
  fresh.respond((url, init) => {
    assert.equal(init.credentials, 'omit');
    assert.equal(init.headers['X-Guest-Credential'], undefined);
    assert.equal(init.body, '{}');
    return response({ ...identity, ...player, appInstanceId: init.headers['X-App-Instance-Id'] });
  });
  await fresh.client.login();
  assert.equal(JSON.parse(fresh.storage.get(key)).guestCredential, identity.guestCredential);

  const interrupted = harness('ios');
  interrupted.respond(() => { throw new Error('Offline during first login'); });
  await assert.rejects(() => interrupted.client.login());
  assert.equal((await interrupted.client.restore()).state, 'new');
  interrupted.respond((url, init) => response({ ...identity, ...player, appInstanceId: init.headers['X-App-Instance-Id'] }));
  assert.equal((await interrupted.client.login()).state, 'authenticated');
  console.log('PASS native: retry first silent login after a network failure');

  const storage = new Map([[key, JSON.stringify(identity)]]);
  const native = harness('android', storage);
  native.respond(() => response(player));
  assert.equal((await native.client.restore()).state, 'authenticated');
  assert.equal(native.calls[0].headers.Authorization, 'Bearer old-access-fixture');
  let renewals = 0;
  native.respond((url, init) => {
    assert.equal(init.credentials, 'omit');
    if (url.endsWith('/auth/guest/login')) {
      renewals++;
      assert.equal(init.headers['X-Guest-Credential'], 'guest-fixture');
      assert.equal(init.headers.Authorization, undefined);
      return response({ appInstanceId: identity.appInstanceId, accessToken: 'new-access-fixture', ...player });
    }
    assert.equal(init.headers['X-Guest-Credential'], undefined);
    if (init.headers.Authorization === 'Bearer old-access-fixture') return response(null, 401, 'access_token_expired');
    return response({ health: 100 });
  });
  await Promise.all([native.client.request('/fuel-prices'), native.client.request('/fuel-prices')]);
  assert.equal(renewals, 1);
  assert.equal(JSON.parse(storage.get(key)).guestCredential, 'guest-fixture');
  const before = storage.get(key);
  native.respond(() => { throw new Error('Offline'); });
  await assert.rejects(() => native.client.restore());
  assert.equal(storage.get(key), before);
  native.respond(() => response(null, 401, 'invalid_guest_credential'));
  assert.equal((await native.client.restore()).state, 'unavailable');
  assert.equal(storage.get(key), before);
  native.respond(() => response({ isLoggedIn: false }));
  assert.equal((await native.client.logout()).state, 'signedOut');
  assert.equal(JSON.parse(storage.get(key)).accessToken, undefined);
  assert.equal(JSON.parse(storage.get(key)).guestCredential, 'guest-fixture');
  const restarted = harness('ios', storage);
  assert.equal((await restarted.client.restore()).state, 'signedOut');
  assert.equal(restarted.calls.length, 0);
  restarted.respond(() => response({ ...identity, ...player }));
  assert.equal((await restarted.client.login()).state, 'authenticated');
  console.log('PASS native: login, secure persistence, restore, serialized renewal, offline/invalid credentials, logout and explicit re-login');

  const failing = harness('ios', new Map([[key, JSON.stringify(identity)]]));
  failing.respond(() => response({ ...identity, ...player, guestCredential: 'new-credential-fixture' }));
  failing.failStorage(true);
  await assert.rejects(() => failing.client.login());
  failing.failStorage(false);
  failing.respond(() => response(player));
  await failing.client.restore();
  assert.equal(JSON.parse(failing.storage.get(key)).guestCredential, 'new-credential-fixture');
  console.log('PASS native: retry one-time credential persistence after temporary storage failure');

  const expiresAt = new Date(Date.now() + 3600000).toISOString();
  const returning = harness('ios', new Map([[key, JSON.stringify({ ...identity, accessTokenExpiresAt: expiresAt })]]));
  let log = [];
  const unsubscribeLog = returning.tokenLog.subscribeTokenLog(entries => { log = entries; });
  assert.equal(await returning.client.resume(), null);
  assert.equal(returning.calls.length, 0); // Valid native tokens need no status API.
  returning.advance(3600001);
  let finishLogin, loginStarted;
  const started = new Promise(resolve => { loginStarted = resolve; });
  returning.respond((url, init) => {
    if (url.endsWith('/auth/guest/login')) {
      assert.equal(init.headers['X-Guest-Credential'], identity.guestCredential);
      loginStarted();
      return new Promise(resolve => { finishLogin = () => resolve(response({
        ...identity, ...player, accessToken: 'renewed-fixture',
        accessTokenExpiresAt: new Date(Date.now() + 7200000).toISOString(),
      })); });
    }
    assert.equal(init.headers.Authorization, 'Bearer renewed-fixture');
    return response({ items: [] });
  });
  const renewal = returning.client.resume();
  await started;
  const waitingRefresh = returning.client.request('/fuel-prices');
  const repeatedReturn = returning.client.resume();
  assert.equal(returning.calls.length, 1); // Price request waits for login.
  assert.deepEqual(Array.from(log.slice(-2), entry => entry.message), ['Token expired', 'Renewing token…']);
  finishLogin();
  assert.equal((await renewal).state, 'authenticated');
  await waitingRefresh;
  await repeatedReturn;
  assert.equal(returning.calls.filter(call => call.url.endsWith('/auth/guest/login')).length, 1);
  assert.equal(log.at(-1).message, 'Token valid');
  unsubscribeLog();
  console.log('PASS native foreground: valid token skips HTTP, expiry renews once, concurrent refresh waits and uses new token');

  const nearExpiry = harness('android', new Map([[key, JSON.stringify({ ...identity,
    accessTokenExpiresAt: new Date(Date.now() + 30000).toISOString(),
  })]]));
  nearExpiry.respond((url, init) => url.endsWith('/auth/guest/login') ? response({ ...identity, ...player,
    accessToken: 'near-expiry-renewed', accessTokenExpiresAt: expiresAt,
  }) : (assert.equal(init.headers.Authorization, 'Bearer near-expiry-renewed'), response({ items: [] })));
  await nearExpiry.client.request('/fuel-prices');
  assert.ok(nearExpiry.calls[0].url.endsWith('/auth/guest/login'));
  const expiredIdentity = { ...identity, accessTokenExpiresAt: new Date(Date.now() - 1000).toISOString() };
  const offline = harness('ios', new Map([[key, JSON.stringify(expiredIdentity)]]));
  offline.respond(() => { throw new Error('Offline return'); });
  await assert.rejects(() => offline.client.resume());
  assert.equal(offline.storage.get(key), JSON.stringify(expiredIdentity));
  offline.respond(() => response(null, 401, 'invalid_guest_credential'));
  assert.equal((await offline.client.resume()).state, 'unavailable');
  assert.equal(offline.storage.get(key), JSON.stringify(expiredIdentity));
  const signedOut = harness('ios', new Map([[key, JSON.stringify({ ...expiredIdentity, signedOut: true })]]));
  assert.equal((await signedOut.client.resume()).state, 'signedOut');
  assert.equal(signedOut.calls.length, 0);
  const noGuest = harness('ios');
  assert.equal(await noGuest.client.resume(), null);
  assert.equal(noGuest.calls.length, 0);
  console.log('PASS native: pre-request early renewal, offline credential retention, invalid identity and logout preservation');

  for (const platform of ['ios', 'web']) {
    const lifecycle = harness(platform);
    let returns = 0;
    const unsubscribe = lifecycle.foreground().subscribeToForeground(() => { returns++; });
    if (platform === 'web') { lifecycle.visibility('hidden'); lifecycle.visibility('visible'); }
    else { lifecycle.appState('active'); lifecycle.appState('background'); lifecycle.appState('active'); lifecycle.appState('active'); }
    assert.equal(returns, 1);
    unsubscribe();
    if (platform === 'web') lifecycle.visibility('visible');
    else { lifecycle.appState('background'); lifecycle.appState('active'); }
    assert.equal(returns, 1);
  }
  console.log('PASS native/web: foreground detection and listener cleanup');

  const web = harness('web');
  let state = 'resumable';
  let csrfFailures = 1;
  web.respond((url, init) => {
    assert.ok(url.startsWith('/'));
    assert.equal(init.credentials, 'same-origin');
    assert.equal(init.headers.Authorization, undefined);
    assert.equal(init.headers['X-Guest-Credential'], undefined);
    if (url.endsWith('/csrf')) return response({ csrfToken: 'csrf-fixture' });
    if (url.endsWith('/status')) return response({ state, ...player });
    assert.equal(init.headers['X-CSRF-Token'], 'csrf-fixture');
    if (csrfFailures-- > 0) return response(null, 400, 'invalid_csrf_token');
    if (url.endsWith('/logout')) { state = 'signedOut'; return response({ isLoggedIn: false }); }
    state = 'authenticated';
    return response({ state, ...player });
  });
  assert.equal((await web.client.restore()).state, 'authenticated');
  assert.equal(web.calls.filter(c => c.url.endsWith('/csrf')).length, 2);
  assert.equal((await web.client.logout()).state, 'signedOut');
  const count = web.calls.length;
  assert.equal((await web.client.restore()).state, 'signedOut');
  assert.equal(web.calls.length, count + 1);
  assert.equal((await web.client.login()).state, 'authenticated');
  assert.equal(web.storage.size, 0);
  console.log('PASS web: cookie-only requests, CSRF refresh, resume, logout and explicit re-login');

  const webReturn = harness('web');
  let browserState = 'authenticated', releaseBrowserLogin, browserLoginStarted;
  const browserStarted = new Promise(resolve => { browserLoginStarted = resolve; });
  webReturn.respond((url, init) => {
    assert.equal(init.credentials, 'same-origin');
    assert.equal(init.headers.Authorization, undefined);
    if (url.endsWith('/status')) return response({ state: browserState, ...player });
    if (url.endsWith('/csrf')) return response({ csrfToken: 'csrf-fixture' });
    if (url.endsWith('/guest/login')) {
      browserLoginStarted();
      return new Promise(resolve => { releaseBrowserLogin = () => {
        browserState = 'authenticated'; resolve(response({ state: browserState, ...player }));
      }; });
    }
    assert.equal(browserState, 'authenticated');
    return response({ items: [] });
  });
  assert.equal((await webReturn.client.resume()).state, 'authenticated');
  assert.equal(webReturn.calls.length, 1);
  browserState = 'resumable';
  const browserRenewal = webReturn.client.resume();
  await browserStarted;
  const browserRefresh = webReturn.client.request('/fuel-prices');
  assert.ok(!webReturn.calls.some(call => call.url.endsWith('/fuel-prices')));
  releaseBrowserLogin();
  await browserRenewal;
  await browserRefresh;
  const browserLoginCount = webReturn.calls.filter(call => call.url.endsWith('/guest/login')).length;
  browserState = 'signedOut';
  assert.equal((await webReturn.client.resume()).state, 'signedOut');
  assert.equal(webReturn.calls.filter(call => call.url.endsWith('/guest/login')).length, browserLoginCount);
  console.log('PASS web foreground: server status, quiet renewal before queued refresh, and cross-tab logout preservation');
})().catch(() => { console.error('Authentication contract check failed'); process.exitCode = 1; });
