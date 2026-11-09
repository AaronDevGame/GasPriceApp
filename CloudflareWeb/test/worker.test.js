import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.js";

const base = "https://pagas.aarondevgame.workers.dev";
const env = {
  ORIGIN_URL: "https://gaspricebackend.onrender.com",
  PROXY_SHARED_SECRET: "test-only-secret-with-at-least-32-characters",
  ASSETS: { fetch: async (request) => new Response(`asset:${new URL(request.url).pathname}`) },
};
function request(path, method = "GET", headers = {}) {
  return new Request(base + path, {
    method,
    headers: { "CF-Connecting-IP": "203.0.113.10", ...headers },
    ...(!["GET", "HEAD"].includes(method) ? { body: "{}" } : {}),
  });
}

test("serves app screens/assets without requiring the proxy secret", async () => {
  for (const path of ["/", "/news", "/news-detail?id=123", "/_expo/static/app.js"]) {
    const response = await worker.fetch(request(path), { ...env, PROXY_SHARED_SECRET: undefined });
    assert.equal(response.status, 200);
    assert.equal(await response.text(), `asset:${new URL(base + path).pathname}`);
  }
});

test("serves the fuel-prices document without shadowing its same-path JSON API", async () => {
  for (const path of ["/fuel-prices", "/fuel-prices/"]) {
    for (const method of ["GET", "HEAD"]) {
      const response = await worker.fetch(request(path, method, { Accept: "text/html,application/xhtml+xml" }), env);
      assert.equal(await response.text(), `asset:${path}`);
    }
  }
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => Response.json({ data: "prices" });
  try {
    const response = await worker.fetch(request("/fuel-prices", "GET", { Accept: "application/json" }), env);
    assert.equal((await response.json()).data, "prices");
  } finally { globalThis.fetch = originalFetch; }
});

test("blocks admin, native auth, MCP, unknown API and ambiguous paths before any fallback", async () => {
  const blockedEnv = { ...env, ASSETS: { fetch: () => assert.fail("must not serve app shell") } };
  for (const path of ["/admin", "/ADMIN/fuel-news/import", "/auth/guest/login", "/auth/status",
    "/mcp", "/.well-known/oauth-authorization-server", "/player/data", "/ai/unknown",
    "/fuel-news/latest/nested", "/fuel-prices/unknown", "/%61dmin/routes"]) {
    assert.equal((await worker.fetch(request(path), blockedEnv)).status, 404, path);
  }
  assert.equal((await worker.fetch(request("/auth/browser/status", "POST"), env)).status, 405);
  assert.equal((await worker.fetch(request("/news", "POST"), env)).status, 405);
});

test("rejects cross-origin mutations before translating Origin, including absent Fetch Metadata", async () => {
  for (const headers of [
    { Origin: "https://evil.example", "Sec-Fetch-Site": "same-origin" },
    { Origin: base, "Sec-Fetch-Site": "cross-site" },
    { Origin: base, "Sec-Fetch-Site": "same-site" },
    { Origin: "null" }, {},
  ]) {
    assert.equal((await worker.fetch(request("/auth/browser/guest/login", "POST", headers), env)).status, 403);
  }
  assert.equal((await worker.fetch(request("/auth/browser/csrf", "GET", { Origin: "https://evil.example" }), env)).status, 403);
  assert.equal((await worker.fetch(request("/auth/browser/status", "GET", { Authorization: "Bearer test" }), env)).status, 400);
  assert.equal((await worker.fetch(new Request("http://pagas.example/auth/browser/csrf"), env)).status, 400);
});

test("forwards login body/cookies/CSRF, replaces spoofed IP metadata and retains all secure cookies", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    assert.equal(url.href, env.ORIGIN_URL + "/auth/browser/guest/login");
    assert.equal(init.method, "POST");
    assert.equal(await new Response(init.body).text(), "{}");
    assert.equal(init.headers.get("Origin"), env.ORIGIN_URL);
    assert.equal(init.headers.get("Sec-Fetch-Site"), "same-origin");
    assert.equal(init.headers.get("X-CSRF-Token"), "test-csrf-token");
    assert.equal(init.headers.get("Cookie"), "__Host-gasprice_csrf=test-cookie");
    assert.equal(init.headers.get("X-GasPrice-Client-IP"), "203.0.113.10");
    assert.equal(init.headers.get("X-GasPrice-Proxy-Secret"), env.PROXY_SHARED_SECRET);
    for (const name of ["X-Forwarded-For", "Forwarded", "X-Forwarded-Host", "X-Forwarded-Proto", "CF-Connecting-IP", "X-Real-IP", "Host"]) {
      assert.equal(init.headers.get(name), null, name);
    }
    assert.equal(init.redirect, "manual");
    assert.equal(init.cache, "no-store");
    assert.equal(init.cf.cacheEverything, false);
    assert.equal(init.cf.cacheTtl, -1, "Cloudflare rejects no-store with any TTL other than -1");
    const headers = new Headers({ "Cache-Control": "public, max-age=3600", Age: "30", "Access-Control-Allow-Origin": "*" });
    headers.append("Set-Cookie", "__Host-gasprice_access=access; Path=/; Secure; HttpOnly; SameSite=Strict");
    headers.append("Set-Cookie", "__Secure-gasprice_guest=guest; Path=/auth/browser; Secure; HttpOnly; SameSite=Strict");
    return Response.json({ data: { state: "authenticated" } }, { headers });
  };
  try {
    const response = await worker.fetch(request("/auth/browser/guest/login", "POST", {
      Origin: base, "Sec-Fetch-Site": "same-origin", "Content-Type": "application/json",
      "X-CSRF-Token": "test-csrf-token", Cookie: "__Host-gasprice_csrf=test-cookie",
      "X-GasPrice-Client-IP": "198.51.100.1", "X-GasPrice-Proxy-Secret": "spoofed",
      "X-Forwarded-For": "198.51.100.2", Forwarded: "for=spoofed", "X-Forwarded-Host": "evil.example",
      "X-Forwarded-Proto": "http", "X-Real-IP": "198.51.100.3",
    }), env);
    assert.equal(response.status, 200);
    assert.equal(response.headers.getSetCookie().length, 2);
    assert.match(response.headers.getSetCookie()[1], /Path=\/auth\/browser; Secure; HttpOnly; SameSite=Strict/);
    assert.equal(response.headers.get("Cache-Control"), "no-store, private, max-age=0");
    assert.equal(response.headers.get("Age"), null);
    assert.equal(response.headers.get("Access-Control-Allow-Origin"), null);
  } finally { globalThis.fetch = originalFetch; }
});

test("forwards all web API route methods and query strings", async () => {
  const originalFetch = globalThis.fetch;
  let received;
  globalThis.fetch = async (url, init) => {
    received = { url: url.href, method: init.method };
    return Response.json({ data: null });
  };
  try {
    for (const [path, method] of [
      ["/auth/browser/csrf", "GET"], ["/auth/browser/status", "GET"], ["/auth/browser/logout", "POST"],
      ["/player/profile", "PATCH"], ["/ai/chat", "POST"], ["/ai/fuel-prices", "POST"],
      ["/fuel-prices?province=Cebu", "GET"], ["/fuel-prices/featured", "GET"],
      ["/fuel-prices/history", "GET"], ["/fuel-prices/adjustments", "GET"], ["/fuel-prices/doe", "GET"],
      ["/fuel-news?limit=20", "GET"], ["/fuel-news/latest", "GET"],
      ["/fuel-news/12345678-1234-1234-1234-123456789abc", "GET"],
      ["/fuel-news/subscription", "GET"], ["/fuel-news/subscription", "PUT"], ["/fuel-news/subscription", "DELETE"],
    ]) {
      assert.equal((await worker.fetch(request(path, method, { Origin: base }), env)).status, 200, path);
      assert.deepEqual(received, { url: env.ORIGIN_URL + path, method });
    }
  } finally { globalThis.fetch = originalFetch; }
});

test("fails closed on missing configuration and missing Cloudflare IP", async () => {
  for (const overrides of [
    { PROXY_SHARED_SECRET: undefined }, { PROXY_SHARED_SECRET: "short" },
    { ORIGIN_URL: "http://insecure.example" }, { ORIGIN_URL: "https://user:pass@example.com" },
    { ORIGIN_URL: "https://example.com/path" }, { ORIGIN_URL: "invalid" },
  ]) {
    assert.equal((await worker.fetch(request("/auth/browser/csrf"), { ...env, ...overrides })).status, 503);
  }
  assert.equal((await worker.fetch(new Request(base + "/auth/browser/csrf"), env)).status, 400);
});

test("preserves backend errors and logout cookie deletion; blocks redirects and handles network failure", async () => {
  const originalFetch = globalThis.fetch;
  try {
    globalThis.fetch = async () => Response.json({ error: { error: "invalid_csrf_token" } }, { status: 400 });
    let response = await worker.fetch(request("/auth/browser/logout", "POST", { Origin: base }), env);
    assert.equal(response.status, 400);
    assert.equal((await response.json()).error.error, "invalid_csrf_token");
    globalThis.fetch = async () => new Response("{}", { headers: {
      "Set-Cookie": "__Host-gasprice_access=; Path=/; Max-Age=0; Secure; HttpOnly; SameSite=Strict",
    } });
    response = await worker.fetch(request("/auth/browser/logout", "POST", { Origin: base }), env);
    assert.match(response.headers.getSetCookie()[0], /Max-Age=0/);
    globalThis.fetch = async () => new Response(null, { status: 302, headers: { Location: "https://other.example" } });
    assert.equal((await worker.fetch(request("/fuel-prices"), env)).status, 502);
    globalThis.fetch = async () => { throw new Error("network"); };
    assert.equal((await worker.fetch(request("/fuel-prices"), env)).status, 502);
  } finally { globalThis.fetch = originalFetch; }
});

test("forwards exact DOE browse GETs and rejects other methods and nested paths", async () => {
  const originalFetch = globalThis.fetch;
  const forwarded = [];
  globalThis.fetch = async (url, init) => {
    forwarded.push({ url: url.toString(), method: init.method });
    return Response.json({ data: { locations: [], selected: null } });
  };
  const browse = (path, method = "GET") => worker.fetch(new Request(
    "https://proxy.example" + path,
    { method, headers: { "CF-Connecting-IP": "203.0.113.10", Accept: "application/json" } },
  ), env);
  try {
    for (const query of ["", "?city=Baler&province=Aurora", "?city=&province=Marinduque"]) {
      const path = "/fuel-prices/doe/browse" + query;
      const response = await browse(path);
      assert.equal(response.status, 200);
      assert.deepEqual(await response.json(), { data: { locations: [], selected: null } });
      assert.deepEqual(forwarded.at(-1), { url: env.ORIGIN_URL + path, method: "GET" });
    }
    const count = forwarded.length;
    assert.equal((await browse("/fuel-prices/doe/browse", "POST")).status, 405);
    assert.equal((await browse("/fuel-prices/doe/browse/nested")).status, 404);
    assert.equal(forwarded.length, count);
  } finally { globalThis.fetch = originalFetch; }
});
