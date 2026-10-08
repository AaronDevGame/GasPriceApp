# Fuel news

Public news reads use the shared `ApiResponse<T>` envelope and do not require guest login or location permission. They read PostgreSQL only and never trigger paid research.

| Route | Behavior |
| --- | --- |
| `GET /fuel-news/latest` | Latest originally published, unexpired, unsuperseded article; `data: null` when none qualifies. No query parameters. |
| `GET /fuel-news?limit=20&cursor=...` | Newest-first history, including expired/superseded articles. Limit 1–50; follow the opaque `nextCursor`. |
| `GET /fuel-news/{id}` | Full article, current revision, original publication/update timestamps and expiry/supersession metadata. |
| `POST /admin/fuel-news/import` | Protected, atomic import; result `imported`, `updated`, or `already_imported`. |
| `GET /admin/fuel-news/{id}/revisions` | Up to 100 latest saved revisions, newest first. |
| `GET /fuel-news/subscription` | Authenticated preferences and whether push sending is enabled on the server. Never returns device tokens. |
| `PUT /fuel-news/subscription` | Authenticated opt-in: `{ "pushToken": "<Expo token>", "includeForecasts": false }`. |
| `DELETE /fuel-news/subscription` | Authenticated opt-out. |

Admin routes require `Authorization: Bearer <ADMIN_API_KEY>` against the direct backend; Cloudflare blocks all admin routes. Subscription routes use existing native bearer + `X-App-Instance-Id` or browser cookies with CSRF on mutations. Public reads are forwarded through Cloudflare and the Expo Web same-origin proxy. Requests retain the existing rate limits; article IDs share one read bucket.

## Import contract

Send an `application/json` object containing `article` and `expectedRevision`. Maximum 32 KiB, including chunked bodies. All fields listed below are required, including nullable fields. Unknown and duplicate JSON properties are rejected at every level.

| Article field | Contract |
| --- | --- |
| `importKey`, `topicKey` | 3–100 lowercase letters, digits and hyphens, starting with a letter/digit. Stable import identity and shared forecast/confirmed topic identity. |
| `category`, `status` | `adjustment` with `forecast`/`confirmed`, or `general` with `general`. |
| `title`, `summary`, `body` | Plain text, respectively 10–160, 20–600, 40–6000 characters. Aim for a 40–60-word summary and 150–250-word article. Body paragraphs use blank lines. |
| `effectiveDatePhilippines` | `yyyy-MM-dd` for adjustments; null for general news. Within 7 days before to 14 days after today's Philippine date. |
| `effectiveAtUtc` | UTC timestamp ending in `Z`, or null if no verified time. Must match the Philippine effective date. |
| `expiresAtUtc` | Future UTC timestamp within 30 days. Forecast expiry cannot exceed midnight following its effective Philippine date. |
| `adjustments` | 1–30 unique fuel/company rows for adjustments; empty for general news. |
| `sources` | 1–10 unique, dated, public HTTPS sources published in the last 14 days. |

Each adjustment contains `fuel` (`gasoline`, `diesel`, `kerosene`), nullable `oilCompany`, `minChangePerLiter`, `maxChangePerLiter`, and `status` (`forecast`/`confirmed`). Signed numeric changes are PHP/liter: negative decreases, positive increases, zero no change. Ranges must be ordered, between -30 and 30, with at most two decimals. Confirmed rows require identical min/max values; a confirmed article requires all its rows confirmed. An absent product is omitted, never represented as zero. These figures do not replace station pump prices or imply separate 91/95 values.

Each source contains `name`, `url`, `publishedAtUtc` (verified UTC timestamp). If the source exposes only a calendar date, use a different source with a verified timestamp until date-only source support is added. Do not invent timestamps. The server checks structure and stored DOE consistency, not independent factual truth; the publishing task must actually read the sources.

For new content use `expectedRevision: null`. A changed existing article requires its current positive revision; stale/missing revision returns HTTP 409. Identical retries ignore expectedRevision and return already_imported without changing timestamps, history or notifications. Source/change arrays are canonicalized before comparing. Keep `importKey`, `topicKey`, category and status immutable. A confirmed announcement uses its own importKey and the forecast's topicKey, supersedes matching forecasts, and prevents later forecasts for that topic. Do not edit superseded forecasts.

Matching confirmed numbers are checked against existing DOE rows for the effective date and optional company. Nationwide exact claims must agree with all stored applicable companies; otherwise use separate company rows. News imports never rewrite DOE rows. Missing DOE rows do not imply verification; retain source evidence.

## Automatic publishing

The existing ChatGPT research task needs a connected authenticated publishing tool or a runner that can execute `scripts/import_fuel_news.py`. Give it `agents/philippines-fuel-news-publisher.md` and this contract. Provision `FUEL_NEWS_API_URL` (the direct HTTPS backend origin) and `FUEL_NEWS_ADMIN_KEY` (the backend admin key) in the runner's secret environment, never in prompts, source or frontend settings. The script follows no redirects and prints only the import outcome, ID and revision. `--dry-run` checks JSON without sending; the server remains authoritative for validation.

The import endpoint and publishing script do not create or change a ChatGPT schedule. Configure and test its publishing connection after deploying the backend. Until that happens, the News tab has an honest empty state; no demonstration news is seeded into production. A text-only scheduled chat cannot write the database by itself.

## Mobile notifications

Subscriptions default to confirmed adjustments; users can include forecasts explicitly. General stories do not send pushes. Publishing writes an outbox row per enabled subscriber in the same transaction as the article/revision. Only changed numeric adjustments or effective dates/times cause another push; wording and source-only edits do not. Older unsent material revisions, expired stories, superseded forecasts and opted-out subscriptions are cancelled before sending.

Enable the worker with `FuelNews__PushEnabled=true` only after configuring Expo/APNs/FCM credentials for the app. If Expo push access-token security is enabled, set server-side `EXPO_ACCESS_TOKEN`. The client uses the real EAS project ID from app configuration; no placeholder ID is committed. Build a new native app after adding expo-notifications. Test opt-in, foreground/background receipt, cold-start article links and opt-out on devices before release. Web does not import native push modules or request notification permissions.

The worker persists tickets, polls receipts after 15 minutes, retries transient failures up to eight times with bounded backoff, and disables DeviceNotRegistered tokens. `accepted` means the provider accepted the notification, not that a person saw it. PostgreSQL session locks prevent parallel workers sending the same batch. Delivery is at least once: a process crash after Expo accepts a request but before the ticket is saved can repeat a notification. Do not advertise exactly-once delivery.

The migration adds news, revision, subscription and delivery tables, preserving existing DOE data. Startup applies it as usual. Push sending is disabled by default; public news and imports work independently of push setup.
