const DEFAULT_ORIGIN = "https://gaspricebackend.onrender.com";

const ALLOWED_ROUTES = new Map([
  ["/ping", new Set(["GET"])],
  ["/health", new Set(["GET"])],
  ["/status", new Set(["GET"])],
  ["/info", new Set(["GET"])],
  ["/routes", new Set(["GET"])],
  ["/auth/status", new Set(["GET"])],
  ["/auth/guest/login", new Set(["POST"])],
  ["/auth/logout", new Set(["POST"])],
  ["/player/data", new Set(["GET", "PATCH"])],
  ["/player/profile", new Set(["PATCH"])],
  ["/ai/chat", new Set(["POST"])],
  ["/ai/fuel-prices", new Set(["POST"])],
  ["/fuel-prices", new Set(["GET"])],
  ["/fuel-prices/history", new Set(["GET"])],
]);

const UNTRUSTED_FORWARDING_HEADERS = [
  "cf-connecting-ip",
  "forwarded",
  "true-client-ip",
  "x-forwarded-for",
  "x-forwarded-host",
  "x-forwarded-proto",
  "x-gasprice-client-ip",
  "x-gasprice-proxy-secret",
  "x-real-ip",
];

const HOP_BY_HOP_HEADERS = [
  "connection",
  "host",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
];

function jsonError(status, error) {
  return Response.json(
    { code: status, message: "request_rejected", data: null, error: { error } },
    {
      status,
      headers: {
        "cache-control": "no-store, private, max-age=0",
        pragma: "no-cache",
      },
    },
  );
}

function isAllowed(pathname, method) {
  return ALLOWED_ROUTES.get(pathname)?.has(method) ?? false;
}

function isPlausibleIpAddress(value) {
  return typeof value === "string" &&
    value.length >= 2 &&
    value.length <= 64 &&
    /^[0-9a-f:.]+$/i.test(value);
}

export default {
  async fetch(request, env) {
    const incomingUrl = new URL(request.url);
    const allowedMethods = ALLOWED_ROUTES.get(incomingUrl.pathname);
    if (!allowedMethods) {
      return jsonError(404, "route_not_available");
    }
    if (!isAllowed(incomingUrl.pathname, request.method)) {
      return jsonError(405, "method_not_allowed");
    }

    if (typeof env.PROXY_SHARED_SECRET !== "string" || env.PROXY_SHARED_SECRET.length < 32) {
      return jsonError(503, "proxy_not_configured");
    }

    const clientIp = request.headers.get("CF-Connecting-IP");
    if (!isPlausibleIpAddress(clientIp)) {
      return jsonError(400, "client_address_unavailable");
    }

    let origin;
    try {
      origin = new URL(env.ORIGIN_URL || DEFAULT_ORIGIN);
    } catch {
      return jsonError(503, "proxy_not_configured");
    }
    if (origin.protocol !== "https:" || origin.username || origin.password ||
        origin.search || origin.hash || origin.pathname !== "/") {
      return jsonError(503, "proxy_not_configured");
    }

    const targetUrl = new URL(incomingUrl.pathname + incomingUrl.search, origin);
    const headers = new Headers(request.headers);
    for (const header of [...UNTRUSTED_FORWARDING_HEADERS, ...HOP_BY_HOP_HEADERS]) {
      headers.delete(header);
    }
    headers.set("X-GasPrice-Client-IP", clientIp);
    headers.set("X-GasPrice-Proxy-Secret", env.PROXY_SHARED_SECRET);

    let originResponse;
    try {
      originResponse = await fetch(targetUrl, {
        method: request.method,
        headers,
        body: request.method === "GET" || request.method === "HEAD"
          ? undefined
          : request.body,
        redirect: "manual",
        cache: "no-store",
        cf: { cacheEverything: false, cacheTtl: 0 },
      });
    } catch {
      return jsonError(502, "origin_unavailable");
    }

    const responseHeaders = new Headers(originResponse.headers);
    responseHeaders.set("Cache-Control", "no-store, private, max-age=0");
    responseHeaders.set("Pragma", "no-cache");
    responseHeaders.set("Expires", "0");
    responseHeaders.delete("Age");

    return new Response(originResponse.body, {
      status: originResponse.status,
      statusText: originResponse.statusText,
      headers: responseHeaders,
    });
  },
};
