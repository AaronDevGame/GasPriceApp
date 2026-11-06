# Pagas web deployment

Deploys the Expo web app and a browser-only API gateway together at
`https://pagas.aarondevgame.workers.dev`. The existing `CloudflareWorker/`
native API deployment is independent. No backend code or database changes are required.

## One-time setup

Use a current Node.js LTS release (Node 24 recommended). Check `node --version`
and `node -e 'console.log(process.version)'` in your terminal; Expo cannot export
with Node 18. The build script uses the same Node executable that starts it.

Use the Cloudflare account that owns the existing `pagas` Worker. From the repository root:

```bash
cd GasPriceClient
npm ci
cd ../CloudflareWeb
npx wrangler login
npx wrangler whoami
```

Check that `whoami` shows the intended account. If the account has several memberships,
select the account owning `pagas` when Wrangler prompts during deployment.

In the Cloudflare dashboard, open **Workers & Pages → pagas → Settings → Variables
and Secrets**. Add a **Secret** named `PROXY_SHARED_SECRET`. Its value must match
Render's existing `CloudflareProxy__SharedSecret` (at least 32 characters). Save it
before deploying. Do not rotate the Render secret just for web: the native proxy uses it too.

Alternatively, when `pagas` already has a Wrangler-managed deployment, use:

```bash
npx wrangler secret put PROXY_SHARED_SECRET
```

Never put the secret in `wrangler.jsonc`, Expo public environment variables, source
files, chat, or command arguments. Wrangler retains existing Worker secrets on deployment.

## Check and deploy

From `CloudflareWeb/`:

```bash
npm test
npm run build
npx wrangler deploy --dry-run
npm run deploy
```

Wrangler runs `npm run build` automatically before both dry-run and real deployment,
so the explicit build is optional. The build exports into `GasPriceClient/dist`
using that project's installed Expo CLI. Install frontend dependencies first.
The deploy replaces `pagas`'s current Hello World deployment. It does not deploy
the native API Worker or the backend. No custom domain is required.

The web client uses relative API paths. `EXPO_PUBLIC_API_URL` configures native
clients only; the web gateway uses `ORIGIN_URL` in this folder's Wrangler config.

For every later update, run `npm test` and `npm run deploy` from this folder.

## Phone checks after deployment

1. Open the HTTPS URL in Safari or Chrome, then test over mobile data with Wi-Fi off.
2. Check featured fuel prices and news, then allow location and request local prices.
3. Test manual location selection and a news article.
4. Reload `/news`, `/fuel-prices`, and `/news-detail?id=...` directly.
5. Close/reopen the page to check session persistence; test logout and reconnection.
6. Confirm `/admin/routes` and `/auth/guest/login` return JSON 404, not the app shell.

`/auth/browser/csrf` should return a JSON envelope with a CSRF token and a secure,
HttpOnly companion cookie. `proxy_not_configured` (503) means the secret or upstream
URL is missing/invalid. `same_origin_required` (403) means the original Origin or
Fetch Metadata was rejected. An HTML API response means API routing is misconfigured.
Check Render availability for backend errors/timeouts. Persist Render's ASP.NET
Data Protection keys so existing antiforgery cookies survive restarts.

## Routing and security

- The Worker runs before assets, so reserved API/admin paths never reach the SPA fallback.
- Exported HTML routes are served by the ASSETS binding; client routes without a file
  fall back to the app shell. Unknown paths outside reserved families may show the app's 404 UI.
- `/fuel-prices` is both an Expo screen and an API endpoint. GET/HEAD requests
  accepting `text/html` serve the screen; the app's `Accept: application/json`
  requests reach Render. Keep this distinction when adding clients or route rules.
- Only explicitly listed routes/methods reach Render. Browser login/status/logout,
  player profile, fuel prices/news, subscriptions and AI calls are supported.
- Browser API requests reject bearer authentication. Mutations require the original
  same-origin `Origin`; cross-site/same-site Fetch Metadata is rejected. CSRF tokens
  and cookies are forwarded unchanged and validated by the backend.
- After checking the public Origin, the gateway translates it to the Render origin,
  matching Render's routing hostname. It removes untrusted forwarding headers and
  supplies authenticated Cloudflare client-IP metadata for backend rate limits.
- All upstream Set-Cookie headers, including deletion, retain their flags and paths.
  No cookie domain rewriting or credentialed CORS is enabled.
- API responses/errors are not cached. The upstream `cache: "no-store"` option
  uses `cf.cacheTtl: -1`; Cloudflare rejects it with `cacheTtl: 0` before contacting
  Render. Upstream redirects are rejected. Worker
  observability is disabled; do not add logging of cookies, auth/CSRF headers or secrets.

The local unit tests use mocked assets/upstream responses and test-only secrets.
They do not call production or prove a deployed browser session works. Phone checks
are still needed after deployment. Plain HTTP `wrangler dev` API requests are rejected;
use the existing Caddy setup for normal local frontend work.
