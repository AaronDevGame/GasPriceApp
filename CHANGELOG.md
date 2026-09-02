# Changelog

All notable changes to this project will be documented in this file.

---

## Unreleased

## [1.8.0] - 2026-09-03

### Added
- Add authenticated `PATCH /player/profile` for changing a guest player's name.

### Changed
- Use `playerName` from `POST /auth/guest/login` only when creating a guest account; subsequent logins preserve the existing name.
- Clients that previously renamed players during login must send `PATCH /player/profile` with a JSON `playerName`, `Authorization: Bearer <access-token>`, and `X-Device-Id` instead.
- Bump project and API version to `1.8.0`.

### Fixed
- Resolve committed merge conflicts in authentication code and the changelog.

### Removed
- Remove the unused `session_active` cookie from guest login and logout responses.

## [1.7.0] - 2026-09-02

### Added
- Issue a one-time, 256-bit random guest credential for new guest accounts and return a one-hour access-token expiration timestamp.
- Allow existing guests to securely upgrade by presenting their previous bearer token once.

### Changed
- Require `X-Guest-Credential` when logging into an existing upgraded guest account.
- Replace deterministic device-derived bearer tokens with random access tokens and store only credential and access-token SHA-256 hashes.
- Revoke access tokens on logout and reject expired tokens on authenticated endpoints.
- Bump project and API version to `1.7.0`.

## [1.6.6] - 2026-09-02

### Added
- Add protected `GET /admin/changelog` for retrieving this changelog as Markdown.

### Changed
- Bump project and API version to `1.6.6`.

## [1.6.5] - 2026-08-22

### Added
- Persist guest login state and return `isLoggedIn` from guest login, authentication status, and logout responses.

### Changed
- Require valid player credentials to log out and reject player-data access after logout.
- Bump project and API version to `1.6.5`.

## [1.6.4] - 2026-08-22

### Added
- Add `accountType` and guest account `createdAt` to successful `GET /auth/status` responses.
- Add guest account `createdAt` and `isNewAccount` to `POST /auth/guest/login` responses.

### Changed
- Bump project and API version to `1.6.4`.

## [1.6.3] - 2026-08-13

### Removed
- Remove the legacy `POST /login` and `POST /logout` aliases; clients must use `POST /auth/guest/login` and `POST /auth/logout`.

### Changed
- Bump project and API version to `1.6.3`.

## [1.6.2] - 2026-08-04

### Changed
- Change ping endpoint  `1.6.2`.

## [1.6.1] - 2026-07-21

### Added
- Add server processing time in milliseconds to the `GET /ping` response.

### Changed
- Bump project and API version to `1.6.1`.

## [1.6.0] - 2026-06-26

### Added
- Add canonical guest auth routes: `POST /auth/guest/login` and `POST /auth/logout`.
- Add canonical admin server lifecycle routes: `GET /admin/server/status`, `POST /admin/server/start`, `POST /admin/server/stop`, and `POST /admin/server/restart`.
- Add protected `GET /admin/routes` for listing admin-only endpoints.

### Changed
- Keep `POST /login`, `POST /logout`, `POST /admin/start`, `POST /admin/stop`, and `POST /admin/restart` as legacy compatibility aliases.
- Mark legacy aliases in route registry responses.
- Centralize route names in `ApiRoutes` and include the new auth endpoints in rate limiting.
- Bump project and API version to `1.6.0`.

## [1.5.2] - 2026-06-25

### Added
- Add `GET /auth/status` to check whether the current device id already has a guest login, returning `hasGuestLogin`, `playerName`, and `playerId`.

### Changed
- List `/auth/status` in the public route registry.
- Bump project and API version to `1.5.2`.

## [1.5.1] - 2026-06-25

### Changed
- Require `X-Device-Id` alongside `Authorization: Bearer <token>` for `/player/data` reads and updates.
- Validate that the provided token and device id belong to the same guest before returning or patching player data.
- Bump project and API version to `1.5.1`.

## [1.5.0] - 2026-06-24

### Added
- Add hybrid `player_data` persistence for gameplay state, including health, money, JSONB position, JSONB inventory, and JSONB extra data.
- Add token-authenticated `GET /player/data` and `PATCH /player/data` routes for player-owned save data.
- Create missing player data on login and backfill existing guests through the `AddPlayerData` EF Core migration.
- Index guest tokens for player route authentication and player data `player_id` for stable lookup.

### Changed
- Bump project and API version to `1.5.0`.

## [1.4.0] - 2026-06-24

### Added
- Add `player_name` persistence for guest accounts, including a unique database index and migration backfill for existing records.
- Accept optional `playerName` in `POST /login` request bodies and return the saved `playerName` in login responses.
- Generate unique fallback names in the `Player ####` format when no player name is provided.

### Changed
- Validate player names by trimming input, limiting names to 24 characters, allowing normal special/non-English characters, and blocking control characters.
- Reorder the `guests` table so `player_id` and `player_name` appear next to `token`.
- Bump project and API version to `1.4.0`.

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
