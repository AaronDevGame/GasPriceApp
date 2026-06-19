# Changelog

All notable changes to this project will be documented in this file.

---

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