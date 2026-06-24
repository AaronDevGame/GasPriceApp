# Changelog

All notable changes to this project will be documented in this file.

---

## [1.3.0] - 2026-06-24

### Added
- Add a display-safe numeric `player_id` for guest accounts, persisted with a unique database index and backfilled through the `AddPlayerId` EF Core migration.
- Return `playerId` from `POST /login` responses alongside the guest device id and token.
- Add a local Docker Compose stack for the API and PostgreSQL.

### Changed
- Update Docker runtime defaults to use `PORT=8080`, expose port `8080`, and let the app bind from the runtime port setting.
- Add startup diagnostics around app building, database migration, and port binding.
- Bump project and API version to `1.3.0`.

## [1.2.9] - 2026-06-23

### Changed
- Reduce all rate limit cooldowns to 1 second for a smoother local and API testing experience.
- Remove obsolete PostgreSQL `TrustServerCertificate` configuration while keeping TLS required for Render database connections.
- Bump project and API version to `1.2.9`.

### Fixed
- Run rate limiting before admin authentication so failed `/admin/*` attempts are throttled.

## [1.1.0] - 2026-02-13

### Added
- Admin authentication middleware
- Rate limiting middleware
- Custom 404 fallback response
- Structured ApiResponse wrapper
- InstanceId in all responses
- ErrorCodes class

### Improved
- Response consistency across endpoints
- Server state handling

### Fixed
- Nullable warnings in ApiResponse
- Minor status logic adjustments

## [1.2.0] - 2026-02-14

### Added
- Start implement versioning
- 404 rate limiting
- Changelog file

## [1.2.1] - 2026-02-18

### Fixed
-  Prevent unbounded dictionary growth in rate limit middleware

## [1.2.2] - 2026-02-18

### Added
-  Health check endpoint

## [1.2.3] - 2026-02-19

### Added
-  Include health, info, and routes in public routes

## [1.2.4] - 2026-02-19

### Added
- .gitignore and .dockerignore

### Changed
- Update Dockerfile to target .NET 10

## [1.2.5] - 2026-06-20

### Added
- Server lifecycle tracking on `/status`: `startedAt` timestamp, `uptimeSeconds`, and human-readable `uptime`
- `/admin/start` now records the start time; `/admin/stop` clears it
- `/admin/restart` endpoint: sets status to running and resets the start time
- Lifecycle history on `/status`: `lastStartedAt`, `lastStoppedAt`, and `restartCount`
- Cumulative `totalUptimeSeconds` / `totalUptime` across all start/stop sessions

### Changed
- Server status is now a typed enum serialized as `Running` / `Stopped` (was loose `start` / `stop` / `stopped` strings)
- `/health` uses a dedicated response model, decoupled from the lifecycle status

## [1.2.6] - 2026-06-20

### Added
- Guest login: `POST /login` issues a deterministic per-device token, `POST /logout` ends the session
- Device identity resolved from the `X-Device-Id` header, a `device_id` cookie, or a server-minted GUID (set as a cookie) when neither is present
- `/login` reports `already_logged_in` while a session is active and `guest_login` otherwise, tracked via a `session_active` cookie that `/logout` clears
- Session is independent per device, and the token stays the same across logout/login (the `device_id` cookie persists; only the session marker is cleared)
- `AuthService` (HMAC-SHA256 over the device id, keyed by `AUTH_SECRET`) in `Auth.cs`
- `/login` and `/logout` listed in the public route registry

## [1.2.7] - 2026-06-21

### Added
- PostgreSQL persistence for guests via EF Core + Npgsql (`Guest` entity, `AppDbContext`, initial `InitGuests` migration)
- `/login` upserts a guest row and records `ip_address`, `user_agent`, best-effort `device_type`, `last_login_at`, and `login_count`
- `/logout` records `last_logout_at` and `logout_count`
- Connection string resolved from `DATABASE_URL`, then `ConnectionStrings:Postgres`, then a local Homebrew default
- Pending migrations are applied automatically on startup
- Forwarded-headers support so the real client IP is captured behind a proxy (e.g. Render)
- TLS for managed Postgres: `DATABASE_URL` connections use `SslMode=Require` with `TrustServerCertificate` so they work on Render

## [1.2.8] - 2026-06-22

### Added
- `version` field on `/info`

### Changed
- Centralize the API version in a single `APIVersion.Version` constant; `/status`, `/health`, and `/info` all read from it
- Update developer contact email

### Fixed
- Display actual message in message field instead of InstanceID
