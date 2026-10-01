# Changelog

## [Unreleased]

### Added
- Add Gas Price and Weekly Changes tabs, with Gas Price opening by default and weekly company fuel adjustments showing increases and decreases.

### Changed
- Remove the Weekly Changes refresh button on web; use browser reload there and pull to refresh on mobile.
- Show the Your area fuel prices in one compact horizontal row while retaining each fuel's color, estimate date, and per-liter label.

## [1.0.2] - 2026-09-29

### Fixed
- Distinguish browser location denial, unavailable positioning, and timeouts; show when coordinates were received but the local fuel-price lookup failed.
- Explain when Expo Web is opened on the app-only port instead of the HTTPS API proxy.
- Distinguish timeouts, invalid API responses, and server/proxy failures instead of reporting all of them as connection failures.
- Allow up to one minute for the first session-status request while the hosted backend starts.

### Changed
- Bump the Expo client version to `1.0.2`.
- Redesign only the Your area card as a station-inspired price board, with large prices, black Diesel, green Gasoline 91, red Gasoline 95, and clear estimate dates; keep other areas and recent updates in their existing layout.
- Hide the Logout button while retaining its code behind a toggle for future testing.
- Replace the Refresh prices button with native pull-to-refresh on iOS and Android; use browser reload on web and show platform-specific recovery guidance.
- Keep all three fuel prices in one horizontal row on mobile and narrow web cards, with compact price typography sized to the available card width.
- Start silently with guest authentication and immediately show saved fuel prices, without player-name input or a login screen.
- Replace the game overview and starter navigation with area prices for Diesel, Gasoline 91, and Gasoline 95 in pesos per liter, with locations and as-of dates.
- Keep logout for testing and use Refresh prices to reconnect silently or refresh the current area.

### Added
- Renew expired native sessions quietly on return to the foreground, and check browser sessions when the page becomes visible; keep prices and controls available while API actions wait for renewal.
- Add a temporary timestamped token log below the header showing expiry, renewal, validity, and retry failures without exposing credentials.
- Request foreground location access on web, iOS, and Android after the first feed loads, then refresh local estimates and prioritize the resolved area.
- Allow manual city/province selection when location is unavailable or declined.
- Show up to five actual price changes in Recent updates, with an empty state for unchanged prices.

### Removed
- Player-data requests and all guest setup, name entry, and player overview UI.

## [1.0.1] - 2026-09-28

### Added
- Add a Caddy-based local HTTPS proxy for testing Expo Web against the deployed Render backend with same-origin browser authentication.

### Fixed
- Translate the localhost browser origin at the development proxy so Render accepts CSRF-protected browser mutations.
- Bump the Expo client version to `1.0.1`.

## [1.0.0] - 2026-09-26

### Added
- Initial Expo Router client release for web, iOS, and Android.
- Guest setup, session restoration, authenticated player overview, loading and retry states.
- Native SecureStore persistence for the public app-instance identifier, guest credential, and short-lived access token.
- Browser authentication through secure HttpOnly cookies and CSRF-protected requests, with no authentication secrets exposed to JavaScript.
- Sign out while retaining the guest credential for explicit “Continue as guest” re-login.
- Explicit confirmation before replacing an unavailable guest identity, plus safe errors for invalid credentials, expired sessions, and network failures.
- Expo-compatible lint configuration and authentication regression checks.
