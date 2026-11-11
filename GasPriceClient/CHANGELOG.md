# Changelog

## [Unreleased]

### Changed
- Hide city reporting counts in City view map labels and selected-city details, and shorten labels to fit the remaining content.
- Replace overlapping city cards with a zoom-based hierarchy: nationwide, island groups, regions, provinces / independent cities, then cities / municipalities. Start with the Philippines overview, aggregate unexpired city report ranges with coverage counts, highlight selected areas, and suppress colliding cards.

### Added
- Add a Philippines-only Fuel Prices map with Google Maps adapters for Android, iOS and web, a City of Manila default, constrained camera and overview zoom, selectable city polygons and collision-aware RON 91 gasoline/diesel range labels. Batch visible-city prices after 750 ms, reuse session boundary/price caches across panning and selection, handle expiry and stale requests, preserve attribution and retain the company list behind a Map/List switch. Configure platform-restricted keys through ignored environment files.
- Add a theme-aware “Should I gas up?” Overview card backed by `/fuel-advice`, with fuel/company selection, green/amber/gray recommendations, API explanations, confirmed 40-liter impact examples, Philippine effective times and source links. Refresh at validity boundaries, on foreground, and with dashboard refresh; keep forecast and default-time labels explicit.
- Show province-level DOE ranges in local company comparisons and the Fuel Prices selector, with explicit coverage labels instead of presenting provincial prices as city-specific.

### Fixed
- Place price boxes within the visible part of a city after panning, size boxes to their contents and prioritize reported ranges over unavailable placeholders. Show the no-prices message only after all visible cities report unavailable.
- Hydrate map controllers from an already cached geographic index on focus, authentication changes and Map/List remounts. Publish the loaded index explicitly so successful cached reads cannot leave the screen stuck before MapRenderer mounts.

## [1.0.5] - 2026-11-06

### Added
- Add the Cloudflare web deployment in `CloudflareWeb/`, serving the Expo static export and a same-origin browser API gateway from the `pagas` Worker. Keep its frontend hosting and browser gateway release notes in this changelog.

### Changed
- Bump the Expo client version to `1.0.5`.
- Redesign the frontend with charcoal surfaces, responsive desktop navigation, and labeled mobile tab icons.
- Replace the Overview ticker with a static news headline, publication date, and article link above local prices.
- Bring location and city selection forward, simplify fuel price cards, and separate company comparisons from area estimates.
- Hide development token logs from the dashboard and improve responsive layouts for featured prices and weekly adjustments.
- Halve ticker repeat spacing, remove headline semicolons, color Confirmed blue, and bold only headline dates.
- Space repeated ticker headlines apart with a responsive gap for easier reading.
- Improve ticker readability with a larger headline, slower scrolling, and ↑/↓ symbols for price directions; keep the summary in the full article.
- Replace the Overview news card above Your area with a compact, looping headline and summary ticker that opens the full article. Start the text immediately at the left edge without extra labels or arrows, repeat seamlessly, and resume from the paused position.

### Fixed
- Allow database-backed news reads time to complete while the hosted server starts up.
- Retry failed older-news requests at their original page while keeping loaded articles.

## [1.0.4] - 2026-10-09

### Added
- Show sourced fuel news in the News tab, full articles with adjustment amounts and Philippine effective dates, and the latest relevant update below the Overview header. News loads independently of guest authentication and location lookup.
- Add native opt-in confirmed fuel alerts, optional forecasts, and notification links to articles for configured Expo builds.

### Changed
- Bump the Expo client version to `1.0.4`.
- Rename the default Gas Price tab to Overview and place four tabs in the order Overview, Fuel Prices, News, Weekly Changes.
- Show a city-searchable DOE company price comparison in the new Fuel Prices tab; reserve the News tab for a future feature.
- Show the total number of available DOE company prices for the selected fuel grade beside the top-five list.
- Show five lowest reported DOE company prices beneath the Your area board, with an expandable, vertically scrollable list of up to ten per fuel grade; check the DOE feed when location research came from another source.
- Show recent DOE prices for featured cities under Luzon, Visayas, and Mindanao on first load; keep those sections visible when location access is denied or a local lookup fails.
- Show only the local price result above the featured sections after location permission is granted, and remove the temporary manual area form from the home screen.
- Start the local HTTPS proxy and open its address when running `npm run web`.
- Show the researched `/ai/fuel-prices` gasoline and diesel ranges in the local price board immediately after a location lookup.
- Show DOE company and fuel-grade price ranges after a location-based lookup when DOE data is available.
- Show available fuel adjustment weeks as selectable date tabs, opening the latest week by default and loading up to 52 grouped weeks.
- Animate week selection into view and the corresponding adjustment cards, with a visible horizontal scrollbar for week tabs on web.
- Let desktop users drag the week tabs horizontally and reveal their scrollbar only while hovered, focused, or dragged.

## [1.0.3] - 2026-10-02

### Added
- Add Gas Price and Weekly Changes tabs, with Gas Price opening by default and weekly company fuel adjustments showing increases and decreases.

### Changed
- Bump the Expo client version to `1.0.3`.
- Show station marks beside correctly styled names on Weekly Changes cards, with effective dates aligned on the right.
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
