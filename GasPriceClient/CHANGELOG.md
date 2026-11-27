# Changelog

## [Unreleased]

### Changed
- Give the desktop navigation header a deep blue background to distinguish it from the charcoal page body.
- Combine area selection and local prices in one card, with inline city search and a location icon that supports retrying after permission is denied.
- Show the desktop GasUp brand lockup on mobile and add space between the fuel adjustment cards and news ticker.
- Rotate up to three recent headlines in a full-width strip directly below the Fuel Prices navigation header, with one story per topic and one price adjustment at a time. Align the FUEL NEWS, CONFIRMED, or FORECAST label beneath GasUp on desktop and extend its background to the left edge of the ticker. Place the headline beside the label, omit a repeated “Confirmed:” prefix on confirmed stories, and provide previous and next arrows.
- Rename the app to GasUp in mobile and desktop headers, the installed app name, and the location permission prompt.

### Fixed
- Keep desktop pages in place during background refreshes by hiding the top refresh indicator.
- Fix a blank homepage on web when the fuel-news headline initializes its animation.
- Show one fuel-price summary per selected area, with DOE-reported ranges when available and an estimate when no DOE report is available.
- Make map price cards clickable so selecting one centers and highlights the area named on the card.

## [1.2.0] - 2026-11-26

### Changed
- Lead with the refueling recommendation in the advice popup and show the local price range below the adjustment timing.
- Show gasoline RON 91 and diesel price ranges beside the adjustments in the header, with City of Manila as the fallback. Mark estimates and DOE report dates, show the underlying report date in advice, and open the company list with the matching fuel selected.
- Show compact gray map cards with an italic “No recent report” when neither fuel has prices. Show the fuel name alongside a single available price range.
- Use a translucent gradient on unselected map price cards so map details remain visible, and a solid deep green fill on the selected card.
- Start the fuel map at city and municipality level in Metro Manila with the City of Manila highlighted. Restore that view and highlight when users center on Metro Manila, and move to the detected area when a location fix succeeds.
- Summarize weekly adjustments by amount and reporting company count in the refueling popup. Count missing amounts separately and show a green decrease range when all current confirmed reports are decreases.
- In the refueling popup, show the amber forecast before confirmed green advice and replace the company selector with a read-only list of the latest Weekly Changes, with logos and per-liter adjustments for the selected fuel.
- Show a green current-decrease panel alongside amber nationwide forecasts when the backend supplies agreeing, unexpired confirmed reports. Identify reporting companies, effective dates and sources, and suppress reassurance for increases, conflicts or future changes.
- Use “Good time to refuel” for effective confirmed rollbacks and show the current per-liter decrease in green in the advice popup. Keep the next forecast separate in amber; forecast-only updates do not receive the confirmed-decrease message.

### Fixed
- Align the adjustment date, amount, and refueling status in the header across screen sizes.
- Use the detected location in both header fuel cards, with local estimates when city map prices are missing and an unavailable message when neither source has a range.
- Keep loaded fuel-price cards visible and anchored to the map while dragging. Update which cards are shown when the map settles.
- Show forecast adjustment ranges and estimated effective dates in the Gasoline and Diesel header notices instead of leaving the amount and date empty. Use amber for unconfirmed forecasts with clearer labels such as “Price drop not yet confirmed” in the notices and advice panel.

## [1.1.2] - 2026-11-23

### Changed
- Bump the Expo client version to `1.1.2` in the app configuration, package manifest, and lockfile. This release changes version metadata only.

## [1.1.1] - 2026-11-21

### Changed
- Bump the Expo client version to `1.1.1`.
- Schedule map-price refreshes at relevant cached expiries instead of checking every 15 seconds. Pause inactive/background maps, fetch current-view prices on return, coalesce overlapping refreshes, and back off failures while preserving city caches and the initial Nationwide/location flow. Honor six-hour server cache deadlines and earlier validity boundaries, including unavailable prices.
- Refresh fuel advice every six hours while active, immediately on native foreground or browser-tab return, and at expiry/effective-time boundaries. Fetch fresh advice on browser reload; pause automatic requests on inactive screens or hidden/backgrounded apps, share requests by fuel/company, and back off failed refreshes before returning to the six-hour interval.

## [1.1.0] - 2026-11-20

### Added
- Show DOE reported, Estimated and Outdated labels on map prices, disclose estimates in broader-area ranges, and show the original report, applied adjustments and sources when a city is selected. Label local area estimates and DOE company lists explicitly.
- Add touch pull-to-refresh on web for Overview, News, Weekly Changes and the Fuel Prices company list, with pull/release feedback and in-place data updates. Preserve map, nested scrolling and native refresh gestures.
- Add clickable gasoline and diesel adjustment summaries in the desktop header and above mobile Overview news, with red increase / green decrease arrows, separate refuel or wait badges, confirmation and Philippine effective times, and detailed advice with sources.
- Select an island group, region, province, or independent city by tapping its boundary or price card on the Fuel Prices map. The selected area shows a gold outline and its fuel price ranges.
- Add a Philippines-only Fuel Prices map with Google Maps adapters for Android, iOS and web, a City of Manila default, constrained camera and overview zoom, selectable city polygons and collision-aware RON 91 gasoline/diesel range labels. Batch visible-city prices after 750 ms, reuse session boundary/price caches across panning and selection, handle expiry and stale requests, preserve attribution and retain the company list behind a Map/List switch. Configure platform-restricted keys through ignored environment files.
- Add a theme-aware “Should I gas up?” Overview card backed by `/fuel-advice`, with fuel/company selection, green/amber/gray recommendations, API explanations, confirmed 40-liter impact examples, Philippine effective times and source links. Refresh at validity boundaries, on foreground, and with dashboard refresh; keep forecast and default-time labels explicit.
- Show province-level DOE ranges in local company comparisons and the Fuel Prices selector, with explicit coverage labels instead of presenting provincial prices as city-specific.

### Changed
- Bump the Expo client version to `1.1.0`.
- Center the detected city outline evenly when location lookup resolves, using GPS as the fallback. Remove the nationwide eastward camera offset from native city/area centering.
- Add Nationwide, Island groups, Regions, Provinces and Cities map navigation buttons. Keep the active level synchronized with manual zoom; tapping a level changes zoom around the current view, while Nationwide restores the Philippines overview.
- Add a left-side map location button that activates after a successful location fix and returns to the detected area. Center the map as soon as coordinates arrive, including when local price lookup fails.
- Restore the latest news headline and news retry message to the top of Fuel Prices, above the page title and area selector.
- Merge Overview and Fuel Prices into the Fuel Prices home tab, leaving News and Weekly Changes as the other tabs. Share city/province selection across DOE summaries, map and company comparisons, preserve view selections, label local estimates separately, collapse recent updates, keep the news headline above prices, and redirect existing `/fuel-prices` links to home.
- Enable web pull-to-refresh outside the Fuel Prices map canvas. Reuse the map refresh and top spinner while preserving camera, selection and gestures inside the interactive map.
- Keep the shared web refresh spinner visible for at least 900 ms so fast foreground refreshes finish opening before the content returns. Data updates stay immediate; longer refreshes hide the spinner when complete.
- Allow web pull-to-refresh to begin on buttons, links and tappable cards; cancel their activation after a drag while preserving taps, inputs, text selection and nested control gestures.
- Refresh Overview, the Fuel Prices company list and map, and Weekly Changes on foreground return using the shared top indicator. Retain current selections, suppress duplicate inline refresh spinners and avoid a second Overview news request.
- Use the shared top refresh indicator when News reloads after returning to the browser or app, keeping automatic refresh without showing a second spinner inside News.
- Show only the pull-to-refresh icon on web while retaining screen-reader status messages, and hide the extra News loading spinner during pull-to-refresh. Keep initial-load and pagination indicators.
- Animate mobile web pull-to-refresh with increasing rubber-band resistance, content displacement, a held spinner during refresh and a spring-like return. Respect reduced-motion preferences.
- Remove the featured “Fuel prices across the Philippines” section from Overview and stop fetching featured prices on launch and refresh.
- Restore the compact desktop header height and show each adjustment date to the left of its refueling badge; keep effective-time and default-time details in the expanded advice.
- Hide city reporting counts in City view map labels and selected-city details, and shorten labels to fit the remaining content.
- Replace overlapping city cards with a zoom-based hierarchy: nationwide, island groups, regions, provinces / independent cities, then cities / municipalities. Start with the Philippines overview, aggregate unexpired city report ranges with coverage counts, highlight selected areas, and suppress colliding cards.
- Move detailed refueling advice from Overview into a dismissible panel opened by the shared Gasoline and Diesel header notices, with the clicked fuel selected. Remove the dashboard introduction and keep mobile access above the tabs.

### Fixed
- Draw web map price cards above boundary fills and highlighted outlines while preserving mouse/touch gesture pass-through.
- Let mouse, touch and zoom gestures pass through map price cards on web and native maps. Keep cards display-only until their click interaction is introduced; map boundaries remain selectable.
- Make web pull-to-refresh accept ordinary text and SVG touch targets, capture gestures before child handlers and claim slow downward pulls before browser scrolling takes over. Keep buttons, links, inputs and nested scroll areas interactive.
- Restore flat green gasoline and yellow diesel nozzle icons and the “Effective” date prefix in the shared desktop and mobile header notices after switching away from the older summary component.
- Resolve committed merge markers in the tab layout, Overview and advice card so the web export builds; retain shared header notices without duplicating Overview advice.
- Share fuel advice across the header, dashboard and detail views; deduplicate and pace requests to respect the API cooldown, reuse valid results across tab changes and responsive remounts, and retain unexpired advice when background refreshes fail.
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
