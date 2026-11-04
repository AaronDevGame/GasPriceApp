# Fuel-news MCP on Render

The backend now contains a Streamable HTTP MCP resource server at `/mcp`. It exposes only `list_news`, `read_article`, and `publish_or_update`. This endpoint is separate from Render's own infrastructure-management MCP service.

The connection is **ChatGPT/Codex → OAuth-protected Render `/mcp` → existing news service/database**. The import service performs the same validation, revision conflict checks, forecast supersession and transactional notification-outbox writes as `/admin/fuel-news/import`. The MCP tools do not make a loopback HTTP request or return credentials. No database migration is needed.

## Where the key goes

Leave the existing `NEWS_API_KEY` in your Render backend's **Environment** settings. It still protects the existing REST news admin routes. Nothing needs to be pasted into chat or copied into Sites for this MCP implementation. `ADMIN_API_KEY` and guest access tokens do not authorize MCP publishing.

The MCP connection instead uses short-lived OAuth access tokens issued for this resource and your allowed publisher identity. The backend validates token signature, exact issuer, resource audience, expiration, subject allowlist and scopes. RS256 and ES256 signing are accepted. Guest login identifies app users, not trusted publishers, so its tokens are deliberately rejected.

## OAuth setup is required before connecting

This code is an OAuth **resource server**, not a login or token-issuing service. It does not create an OAuth account, authorization endpoint, client registration endpoint, or consent screen. Choose an OAuth provider first (for example Auth0, Keycloak or Entra ID), then configure it to:

1. Serve HTTPS authorization-server/OpenID discovery and public signing keys. Its discovery `issuer` must exactly match `Mcp__Authority`, including a trailing slash if present.
2. Issue signed JWT **access tokens** with audience `https://backendserver-rtqi.onrender.com/mcp`. Do not use an ID token or the guest login token.
3. Define the scopes `fuel-news:read` and `fuel-news:publish`. Grant publishing only to your publisher account. The token needs both scopes to publish.
4. Support the connecting MCP client's OAuth flow: authorization code with PKCE S256, resource indicators for this resource, and a compatible registration method (dynamic registration, client metadata documents, or a pre-registered client). If pre-registering, use the exact redirect URI shown by the client; do not guess a callback URL. Provider support must be verified before choosing it.
5. Obtain your publisher account's stable `sub` claim through the provider's account/admin interface. Configure that subject as an allowlisted identity. An email address is not a substitute for `sub`.

The backend does not need the OAuth client's secret to verify tokens. If your client registration requires a client secret, enter it only in that client's supported secure configuration interface. Never send it through chat. Creating a provider account and registering the client remain deployment steps; installing this code alone does not complete them.

## Render configuration

On the **existing backend service**, set these non-secret values under **Environment**, then deploy the backend code:

| Variable | Value |
| --- | --- |
| `Mcp__Enabled` | `true` after the OAuth settings below are ready; otherwise omit or `false` |
| `Mcp__Authority` | Exact HTTPS issuer URL from your OAuth provider |
| `Mcp__Resource` | `https://backendserver-rtqi.onrender.com/mcp` |
| `Mcp__AllowedSubjects` | Your provider-issued publisher `sub`; comma-separated for multiple publishers |
| `Mcp__PublishEnabled` | `false` for initial connection checks |

The double underscores are ASP.NET Core's environment-variable representation of nested configuration. Empty/missing issuer, resource or subject settings cause startup to reject an explicitly enabled MCP configuration. With `Mcp__Enabled` omitted/false, `/mcp` returns HTTP 503 and the rest of the backend remains available. The default deployment adds no publishing permission.

Use the direct Render HTTPS endpoint, not the Cloudflare proxy, which does not forward these routes. The protected resource metadata document is at:

`https://backendserver-rtqi.onrender.com/.well-known/oauth-protected-resource/mcp`

It lists the resource, authorization server and supported scopes, never secrets. Unauthorized `/mcp` requests return HTTP 401 and advertise this URL in `WWW-Authenticate`. Valid tokens with an unlisted subject or missing read scope return HTTP 403. Only header bearer authentication is supported; cookies and query-string tokens are not accepted. Browser requests with an `Origin` different from the configured resource origin are rejected; server-to-server MCP clients omit `Origin`.

## Connect and enable

1. Configure the provider and deploy this backend with `Mcp__PublishEnabled=false`.
2. Add `https://backendserver-rtqi.onrender.com/mcp` as a remote MCP server in your MCP client, using its supported OAuth connection flow. The previous Sites connection does not automatically change destinations. Check client/plan support for custom remote MCP servers before expecting the connection to appear.
3. Sign in as the allowlisted publisher, discover the tools and call `list_news`/`read_article`. Verify that no token, guest token, `NEWS_API_KEY` or `ADMIN_API_KEY` alone authorizes `/mcp`, and that another OAuth user's token cannot connect.
4. Explicitly set `Mcp__PublishEnabled=true` in Render and redeploy when ready. An authorized caller still needs `fuel-news:read` and `fuel-news:publish`. Read-only tokens cannot publish even with the switch on.
5. Publish only a real, researched article after verifying its source timestamps. Read it back and retain its revision for later updates. Imports may queue subscriber notifications through the existing outbox. Do not test with sample news in production.
6. Once the Render connection is verified, disconnect the old Sites MCP connection in your client to avoid calling the old tools accidentally. There is no automatic migration or deletion of the Sites app.

Use `Mcp__PublishEnabled=false` to stop MCP writes while leaving MCP reads available; use `Mcp__Enabled=false` to disable the MCP endpoint entirely. These switches do not change the existing REST news admin route's authentication or behavior.

## Protocol and limits

- Uses the official C# MCP SDK, stateless Streamable HTTP, and per-request OAuth authorization. No local session affinity is required.
- Tool results and errors follow MCP's protocol envelopes. Existing REST routes retain their `ApiResponse<T>` envelopes. HTTP boundary errors use the existing API helpers.
- The entire JSON-RPC body is limited to 32 KiB, including chunked bodies. JSON batches, duplicate keys, malformed JSON, unknown tool arguments and unknown/missing article properties are rejected before invocation.
- MCP requests share a 60-per-minute IP window, including authentication failures and case/trailing-slash route variations. Discovery has a separate one-second cooldown. Forwarded client IP resolution runs first. Limits are per process, matching the existing backend rate-limit model.
- `list_news` accepts `limit` (1–50, default 20) and nullable opaque `cursor`. `read_article` accepts a UUID `id`. `publish_or_update` accepts `article` and an explicitly present nullable `expectedRevision`, matching [the import contract](fuel-news.md#import-contract).
- The SDK exposes a full article input schema. `publish_or_update` is marked destructive and not read-only because it changes production and can cause notifications. The server does not research or verify factual accuracy on the caller's behalf.

## Local verification

From the repository root:

```sh
dotnet build GasPriceApp.sln
dotnet run --project GasPriceBackend/tests/FuelNewsChecks -- --mcp-only
dotnet run --project GasPriceBackend/tests/FuelNewsChecks -- --auth-only
```

The MCP checks host the real endpoint locally with ephemeral signing keys, a static test issuer and a deliberately unavailable database. They exercise discovery, JWT validation, allowlists/scopes, initialization/tool schemas, the publish switch, article validation before database access, bounded/strict JSON, Origin rejection and rate limiting. They never publish news, contact a real OAuth server or use production secrets. For database import/outbox behavior, use the existing isolated PostgreSQL smoke checks in `tests/fuel_news_smoke.py` when PostgreSQL is available.

References: [official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), [MCP authorization](https://modelcontextprotocol.io/specification/latest/basic/authorization), [Render environment variables](https://render.com/docs/configure-environment-variables).
