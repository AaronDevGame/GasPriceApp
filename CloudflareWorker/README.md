# GasPrice API proxy

This Cloudflare Worker exposes the public API at
`https://gasprice-api-proxy.aarondevgame.workers.dev` and forwards registered
native and diagnostic routes to `https://gaspricebackend.onrender.com`. It blocks
browser-cookie, admin, and unknown routes, removes caller-supplied forwarding
headers, forwards the Cloudflare-observed client IP with an authenticated header,
and marks every response as non-cacheable.

## Required configuration

Generate one random secret containing at least 32 characters. Store the same
value in these two secret/configuration locations; never commit it:

1. Render: `CloudflareProxy__SharedSecret`
2. Cloudflare Worker secret: `PROXY_SHARED_SECRET`

With Wrangler authenticated to the correct Cloudflare account, configure and
deploy the Worker from this directory:

```bash
npx wrangler secret put PROXY_SHARED_SECRET
npx wrangler deploy
```

Deploy the backend support and set its Render environment variable before
deploying the Worker. A Worker without its secret fails closed with HTTP 503.

Configure native Expo builds with this public value (it is not a secret):

```text
EXPO_PUBLIC_API_URL=https://gasprice-api-proxy.aarondevgame.workers.dev
```

The Expo web client intentionally uses same-origin relative API paths instead.

Run the local Worker checks with `npm test`. Do not use a production secret in
`.dev.vars`; local tests supply a test-only value.
