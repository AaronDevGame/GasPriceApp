# Changelog

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
