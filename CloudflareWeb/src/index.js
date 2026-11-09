// Browser-only entry point. The native API Worker remains independently deployed.
const ROUTES = new Map([
  ["/auth/browser/csrf", ["GET"]],
  ["/auth/browser/status", ["GET"]],
  ["/auth/browser/guest/login", ["POST"]],
  ["/auth/browser/logout", ["POST"]],
  ["/player/profile", ["PATCH"]],
  ["/fuel-prices", ["GET"]],
  ["/fuel-prices/featured", ["GET"]],
  ["/fuel-prices/history", ["GET"]],
  ["/fuel-prices/adjustments", ["GET"]],
  ["/fuel-prices/doe", ["GET"]],
  ["/fuel-prices/doe/browse", ["GET"]],
  ["/fuel-prices/map/prices", ["POST"]],
  ["/fuel-prices/map/geography/index", ["GET"]],
  ["/fuel-news", ["GET"]],
  ["/fuel-advice", ["GET"]],
  ["/fuel-news/latest", ["GET"]],
  ["/fuel-news/subscription", ["GET", "PUT", "DELETE"]],
  ["/ai/chat", ["POST"]],
  ["/ai/fuel-prices", ["POST"]],
]);

const STRIPPED_HEADERS = [
  "cf-connecting-ip", "forwarded", "true-client-ip", "x-forwarded-for",
  "x-forwarded-host", "x-forwarded-proto", "x-real-ip",
  "x-gasprice-client-ip", "x-gasprice-proxy-secret",
  "connection", "host", "keep-alive", "proxy-authenticate",
  "proxy-authorization", "te", "trailer", "transfer-encoding", "upgrade",
  "referer",
];

function error(status, code) {
  return Response.json(
    { code: status, message: "request_rejected", data: null, error: { error: code } },
    { status, headers: { "Cache-Control": "no-store, private, max-age=0", Pragma: "no-cache" } },
  );
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.protocol !== "https:") return error(400, "browser_https_required");

    // Reject ambiguous encodings before either assets or the backend can decode them.
    if (/%|\\/.test(url.pathname)) return error(404, "route_not_available");
    const path = url.pathname;
    if (/^\/admin(?:\/|$)/i.test(path)) return error(404, "route_not_available");

    // Expo's fuel-prices screen shares its URL with the JSON API. Browser document
    // navigations request HTML; the app's API client explicitly requests JSON.
    if ((path === "/fuel-prices" || path === "/fuel-prices/") &&
        ["GET", "HEAD"].includes(request.method) &&
        request.headers.get("Accept")?.split(",").some((type) => type.trim().split(";")[0] === "text/html")) {
      return env.ASSETS.fetch(request);
    }

    const geography = /^\/fuel-prices\/map\/geography\/(?:index|ph-2026q3-v1\/[0-9]{10}-[0-9]{2})$/.test(path);
    const methods = ROUTES.get(path) ?? (geography ? ["GET"] : undefined) ??
      (/^\/fuel-news\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(path)
        ? ["GET"] : undefined);
    if (!methods) {
      // Reserved API families must never fall back to the HTML app shell.
      if (/^\/(auth|player|fuel-prices|fuel-news|fuel-advice|ai|mcp|\.well-known)(\/|$)/i.test(path)) {
        return error(404, "route_not_available");
      }
      if (!["GET", "HEAD"].includes(request.method)) return error(405, "method_not_allowed");
      return env.ASSETS.fetch(request);
    }
    if (!methods.includes(request.method)) return error(405, "method_not_allowed");

    const sourceOrigin = request.headers.get("Origin");
    const site = request.headers.get("Sec-Fetch-Site");
    const mutation = !["GET", "HEAD"].includes(request.method);
    // Validate the ORIGINAL browser origin before translating it for Render's host.
    // Keep Sec-Fetch-Site and X-CSRF-Token intact for backend antiforgery validation.
    if ((sourceOrigin !== null && sourceOrigin !== url.origin) ||
        (site !== null && site !== "same-origin" && site !== "none") ||
        (mutation && sourceOrigin !== url.origin)) {
      return error(403, "same_origin_required");
    }
    if (request.headers.has("Authorization")) return error(400, "browser_cookie_auth_required");

    if (typeof env.PROXY_SHARED_SECRET !== "string" || env.PROXY_SHARED_SECRET.length < 32) {
      return error(503, "proxy_not_configured");
    }
    const clientIp = request.headers.get("CF-Connecting-IP");
    if (!clientIp || clientIp.length > 64 || !/^[0-9a-f:.]+$/i.test(clientIp)) {
      return error(400, "client_address_unavailable");
    }

    let origin;
    try { origin = new URL(env.ORIGIN_URL); } catch { return error(503, "proxy_not_configured"); }
    if (origin.protocol !== "https:" || origin.username || origin.password ||
        origin.search || origin.hash || origin.pathname !== "/") {
      return error(503, "proxy_not_configured");
    }
    const headers = new Headers(request.headers);
    for (const name of STRIPPED_HEADERS) headers.delete(name);
    headers.set("X-GasPrice-Client-IP", clientIp);
    headers.set("X-GasPrice-Proxy-Secret", env.PROXY_SHARED_SECRET);
    if (sourceOrigin !== null) headers.set("Origin", origin.origin);

    let upstream;
    try {
      upstream = await fetch(new URL(path + url.search, origin), {
        method: request.method,
        headers,
        body: mutation ? request.body : undefined,
        redirect: "manual",
        cache: "no-store",
        // Cloudflare requires -1 (not 0) when combined with cache: "no-store".
        cf: { cacheEverything: false, cacheTtl: -1 },
      });
    } catch { return error(502, "origin_unavailable"); }
    // APIs return JSON. Never expose redirects to another host or forward credentials there.
    if (upstream.status >= 300 && upstream.status < 400 && !(geography && upstream.status === 304)) return error(502, "unexpected_origin_redirect");

    // Copy Headers directly to retain every Set-Cookie separately (including logout).
    const responseHeaders = new Headers(upstream.headers);
    if (!(geography && (upstream.status === 200 || upstream.status === 304))) {
      responseHeaders.set("Cache-Control", "no-store, private, max-age=0");
      responseHeaders.set("Pragma", "no-cache");
      responseHeaders.set("Expires", "0");
      responseHeaders.delete("Age");
    }
    for (const name of [...responseHeaders.keys()]) {
      if (name.startsWith("access-control-")) responseHeaders.delete(name);
    }
    return new Response(upstream.body, {
      status: upstream.status,
      statusText: upstream.statusText,
      headers: responseHeaders,
    });
  },
};
