import assert from "node:assert/strict";
import test from "node:test";

import worker from "../src/index.js";

const env = {
  ORIGIN_URL: "https://gaspricebackend.onrender.com",
  PROXY_SHARED_SECRET: "a-test-secret-that-is-at-least-32-characters",
};

test("blocks admin routes before reaching the origin", async () => {
  const originalFetch = globalThis.fetch;
  globalThis.fetch = () => {
    throw new Error("origin must not be called");
  };
  try {
    const response = await worker.fetch(
      new Request("https://proxy.example/admin/routes", {
        headers: { "CF-Connecting-IP": "203.0.113.10" },
      }),
      env,
    );
    assert.equal(response.status, 404);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("rejects unsupported methods on public routes", async () => {
  const response = await worker.fetch(
    new Request("https://proxy.example/health", {
      method: "POST",
      headers: { "CF-Connecting-IP": "203.0.113.10" },
    }),
    env,
  );
  assert.equal(response.status, 405);
});

test("replaces spoofed forwarding headers with authenticated values", async () => {
  const originalFetch = globalThis.fetch;
  let forwardedRequest;
  globalThis.fetch = async (input, init) => {
    forwardedRequest = { input: input.toString(), init };
    return Response.json({ ok: true }, { headers: { "Cache-Control": "public, max-age=3600" } });
  };
  try {
    const response = await worker.fetch(
      new Request("https://proxy.example/fuel-prices?province=Cebu", {
        headers: {
          "CF-Connecting-IP": "203.0.113.10",
          "X-Forwarded-For": "198.51.100.20",
          "X-Real-IP": "198.51.100.21",
          "X-GasPrice-Client-IP": "198.51.100.22",
          "X-GasPrice-Proxy-Secret": "attacker-value",
        },
      }),
      env,
    );

    assert.equal(forwardedRequest.input,
      "https://gaspricebackend.onrender.com/fuel-prices?province=Cebu");
    assert.equal(forwardedRequest.init.headers.get("X-Forwarded-For"), null);
    assert.equal(forwardedRequest.init.headers.get("X-Real-IP"), null);
    assert.equal(forwardedRequest.init.headers.get("X-GasPrice-Client-IP"), "203.0.113.10");
    assert.equal(forwardedRequest.init.headers.get("X-GasPrice-Proxy-Secret"),
      env.PROXY_SHARED_SECRET);
    assert.equal(response.headers.get("Cache-Control"), "no-store, private, max-age=0");
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test("fails closed when the shared secret is missing", async () => {
  const response = await worker.fetch(
    new Request("https://proxy.example/health", {
      headers: { "CF-Connecting-IP": "203.0.113.10" },
    }),
    { ORIGIN_URL: env.ORIGIN_URL },
  );
  assert.equal(response.status, 503);
});

test("rejects removed player-data routes", async () => {
  for (const method of ["GET", "PATCH"]) {
    const response = await worker.fetch(new Request("https://proxy.example/player/data", { method }), env);
    assert.equal(response.status, 404);
  }
});
