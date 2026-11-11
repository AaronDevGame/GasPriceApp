# Fuel price map

The Fuel Prices screen defaults to a map, with the existing company list available through its switch. Other screens and existing price feeds are unchanged. Default view fits the whole Philippines; browsing does not request location permission or call AI/geocoding.

## Required backend deployment

The client depends on the backend and proxy work committed in `73c4451` and `22a721e` (already in the base of this frontend branch):

- Public `GET /fuel-prices/map/geography/index`
- Public `GET /fuel-prices/map/geography/{version}/{fileId}`
- Authenticated `POST /fuel-prices/map/prices`

No backend/proxy files are changed by this frontend phase. Those routes must be available at the configured native API origin and the web same-origin gateway before live data will load. No deployment was performed here. The app checks `countryCode: PH` and rejects price feeds with a different version, geographic scope, or fuel grades.

## Platform configuration

Use Node 24 (or an Expo-supported Node version) and install dependencies with `npm ci`. This app uses Expo SDK 57, React Native 0.86 and `react-native-maps` 1.27.2 installed with `npx expo install`.

Copy `.env.example` to **ignored `.env.local`** and provide the appropriate environment values, or configure them in the build service environment. Do not commit real keys or environment files. API keys used by client SDKs are discoverable in distributed apps; restrict them in Google Cloud rather than relying on secrecy. `app.config.js` injects native keys into the map config plugin, while `extra.fuelMap` exposes only configuration booleans. The web key is deliberately public JavaScript configuration.

| Platform | Integration | Required configuration |
| --- | --- | --- |
| Android | `react-native-maps`, explicit Google provider | `GOOGLE_MAPS_ANDROID_API_KEY`, `ANDROID_APPLICATION_ID`; enable Maps SDK for Android; restrict key to package and signing-certificate SHA-1 |
| iOS | `react-native-maps`, explicit Google provider | `GOOGLE_MAPS_IOS_API_KEY`, `IOS_BUNDLE_IDENTIFIER`; enable Maps SDK for iOS; restrict key to bundle identifier |
| Web | Google Maps JavaScript API, separate `.web.tsx` renderer | `EXPO_PUBLIC_GOOGLE_MAPS_WEB_API_KEY`; enable Maps JavaScript API; restrict key to actual production/development HTTP referrers |

Native API origin remains `EXPO_PUBLIC_API_URL`; web API requests remain same-origin. Native package/bundle identifiers must be the actual registered identifiers, not placeholders. Use separate keys and restrictions for the three platforms and include every required development/release signing certificate on Android. Restart Metro after changing public environment values; changing native keys/plugins requires a **new native binary**, not an OTA-only update.

Build native development clients through EAS, e.g. `npx eas-cli@latest build --profile development --platform android` / `ios`, after configuring the project/build profiles and identifiers. No EAS profile, signing credentials, Xcode project or Android project is created by this feature. Test this Google provider integration in development builds. The iOS Google provider is intentionally gated in Expo Go, which is not a substitute for verifying the custom native Google SDK configuration. Builds without the corresponding configuration show an unavailable-map state rather than mounting an unconfigured native provider.

Google Cloud requires billing enabled. Native maps without map IDs use the Maps SDK SKU; web uses Dynamic Maps, and native map IDs can also trigger Dynamic Maps billing. This feature does not set a native map ID, use Places, or request geocoding. Verify current rates in Google Cloud before release; see [Google SKU triggers](https://developers.google.com/maps/billing-and-pricing/sku-details/maps) and [pricing](https://developers.google.com/maps/billing-and-pricing/pricing). The web loader requests `region=PH` for basemap localization; the actual data restriction comes from our Philippine index, not that SDK parameter.

## Camera and rendering policy

- Start by fitting the national bounds from the index.
- Derive the navigation rectangle from the national dataset extent plus 0.25 degrees of padding. This is a navigation rectangle, not a newly traced administrative boundary.
- Calculate minimum zoom from the national bounds with 35 pixels of padding and the map dimensions, recalculating on layout changes; maximum zoom is 18. Rotation and tilt are disabled.
- Web uses Google camera restriction with a separately calculated overview minimum zoom.
- Native requests Google bounds but also clamps camera centers during movement and verifies center/zoom at idle. Inspection of the pinned library found its legacy `setMapBoundaries` command is not forwarded on the Fabric path, so correctness does not depend on that command. Native enforcement can correct the camera during/after gestures and must be device-tested for smoothness.
- Restriction applies to camera centers. Visible corners at overview can include surrounding sea/neighboring basemap land. We load only Philippine administrative polygons and Philippine city prices; we do not claim the basemap itself contains only Philippine tiles.
- Show one geographic level at a time: nationwide, Luzon/Visayas/Mindanao, regions, provinces / independent cities, then cities / municipalities. Thresholds are relative to the zoom needed to fit the national extent at the current canvas size: below +0.75 is nationwide, +0.75 island groups, +2 regions, +3.5 provinces, and +5 cities. City detail polygons also require no more than 160 candidate cities.
- At island group, region and province zooms, tap land or a price card to highlight the area in gold. Each area has an outline formed by merging its member cities and municipalities. Offshore islands belong to the same selection, and enclaves such as Baguio can be selected separately at province level. Selecting an area gives its card first priority. Selection details remain readable below the map. Pinch or map zoom controls advance through levels; changing levels removes highlights that do not belong to the current level.
- Province membership uses `provinceId`. Highly urbanized / independent cities without a province stay separate at that level; geographic storage groups are not treated as administrative provinces. The island grouping uses the index's regions, including Negros Island Region in Visayas and BARMM in Mindanao.
- The client bundles simplified area outlines for the matching geography version. Regenerate them from the versioned backend city files with `python3 scripts/prepare-area-boundaries.py` in the client directory (Shapely 2.0.7). When the backend serves a different version, cards fall back to highlighting the selected area using that version’s city files.
- MultiPolygons retain their outer rings and holes. Exact rectangle/polygon intersection refines bounding-box candidates once geometry is available.
- Polygon taps highlight their city; no tap is required to fetch visible prices. Existing provider attribution remains unobscured. NAMRIA/PSA/OCHA, CC BY 3.0 IGO, James Faeldon/MIT and modification notices are displayed below the map with source/license links.

## Loading and cache behavior

`MapDataCache` is shared for the app session. Index reads and each version/file download coalesce in flight. Public geometry uses a separate credential-free transport with HTTP caching enabled, four concurrent boundary downloads, a timeout, and a bounded Retry-After retry. Prices go through the existing native/browser authenticated client, preserving cookie/CSRF and token renewal behavior.

After map movement ends, a 750 ms debounce uses the index to find intersecting file/city bounds. Matching island, region and province views use bundled outlines. City detail and nationwide selections load the necessary city files. Prices are fetched in one batch for missing/expired IDs of every member of each visible area. This includes members outside the viewport, so panning within an area does not change its reported range. Nationwide requests include all 1,642 cities, without downloading unselected nationwide polygons. Once geometry is loaded, precise intersections filter the rendered cities. One detail file may contain several cities in the same geographic group.

Boundaries/features are keyed by boundary version + file/PSGC. Prices are keyed by version + PSGC + RON 91. The same caches are used for panning and tapping, including Imus. Revisiting a cached city publishes available polygons/prices synchronously before the debounce or network work. Missing-price results are cached until their backend expiry too. Caches persist through tab/screen remounts, but not app/process reloads. They are currently held in session memory rather than persistent storage; nationwide raw geometry totals about 7.2 MB before JavaScript object overhead.

Only expired/missing prices are requested. Backend `expiresAtUtc` is authoritative; expired labels are suppressed and stationary visible prices are checked every 15 seconds and on foreground return. This means a stationary expiry refresh can start up to 15 seconds after expiry. Index validity is five minutes; file content is immutable per version. Explicit sign-out/account changes invalidate price caches and invalidate older authorized completions, while public boundary caches remain reusable.

Moving invalidates pending debounce work and the view generation. Shared in-flight requests may finish to populate reusable caches; old responses cannot restore an old viewport or overwrite newer generated prices. The code deliberately disregards obsolete view work rather than aborting a shared download another viewport may need. Failures clear in-flight entries so retry can work; a geometry failure does not block the independent price batch. Errors are shown with retry controls while valid cached data remains available.

## Price labels

Each card shows the area name and gasoline **RON 91** and ordinary **DIESEL** ranges. Above City view, cards and selected-area details also show the number of cities / municipalities reporting out of the area's total. City view omits these counts. Area ranges combine the minimum and maximum of unexpired city reports for each grade. These are **reported ranges, not averages or full station coverage**. Missing grades use `—`; missing reports never contribute zero. Incomplete loads are marked explicitly. No province feed is substituted for missing city reports.

Cards use the current geographic level, independently of detailed geometry. They try alternate positions within the visible area bounds, with the selected area and then reported prices receiving placement priority. Colliding cards are suppressed until zooming or panning creates room. Every city polygon remains selectable at detail scale; selecting one reserves its card's placement first. Cards avoid attribution, hide during movement and recompute at idle.

## Verification and remaining device checks

```sh
npm run lint
npm run typecheck
npm run test:map
npm run test:auth
npm run test:fuel
npm run test:advice
npx expo export --platform all --output-dir /tmp/gasprice-map-client-export
npx expo-doctor
```

Expo Doctor currently passes 20/21 checks; the remaining check lists ten existing Expo SDK patch-version mismatches already present in the base lockfile. No existing dependency versions were changed for this feature. npm also reports 25 dependency audit findings; those require a separate dependency review rather than an automatic major-version update here.

Map tests use the real pinned Imus/Manila geometry with controlled time/transport. They cover shared caches, in-flight deduplication, debounce cancellation, expiry, old response completion, logout invalidation, missing grades/scope rejection, independent failures, polygon holes/intersections, responsive level thresholds, all three island groups and all 18 regions, independent-city handling, aggregate ranges and coverage, nationwide member loading, selected-area geometry, and collision suppression/selection priority. Exports verify web static rendering and Android/iOS JavaScript/Hermes bundling; they do **not** verify signed native binaries or the live Google SDK.

Before release, use restricted real keys and the deployed backend to test live web tiles/authentication/labels and both native development builds on devices. Check drag/pinch inside the parent scroll view, camera correction smoothness at all edges, minimum zoom on different aspect ratios, text bitmap rendering on Android, iOS Google provider setup, polygon holes, attribution visibility, offline/resume, rapid Imus/Manila revisits, unavailable prices, and supported screen-reader behavior. Do not interpret successful bundling as device verification.
