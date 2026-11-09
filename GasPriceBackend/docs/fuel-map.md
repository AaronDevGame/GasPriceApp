# Philippine fuel-map backend

This phase adds backend data and reads only. No Expo changes, map keys, deployment, database migration, or API version bump are required. Existing feeds retain their contracts and fallback behavior.

## Boundary provenance and license

Dataset version: `ph-2026q3-v1`. Source: [James Faeldon's Philippines JSON Maps](https://github.com/faeldon/philippines-json-maps/tree/680e32e62baeccbcd8845d3b1387bc67c1baf78a), commit `680e32e62baeccbcd8845d3b1387bc67c1baf78a` (2026-11-07). Its manifest identifies GeoJSON version 1.1.1, PSGC 2026 Q3, build 2026-11-07, and upstream shapefile release v1.0.1 at commit `be65ee15b10bba45e310f6edeec1bd45afcb33b6`.

The original geometry is NAMRIA/PSA through [OCHA COD-AB on HDX](https://data.humdata.org/dataset/cod-ab-phl), release `phl_admbnda_admN_psa_namria_20231106` (2023-11-06), with subsequent administrative changes documented by the upstream maintainer. The upstream README states OCHA's May 2026 version 3 reorganizes those shapes. The November 2026 package date is **not** a new survey date. These are administrative visualization boundaries, not cadastral or navigational authority. Direct HDX access was blocked during preparation; provenance was checked against the pinned maintainer documentation, not independently against the HDX download.

The GeoJSON repository is MIT; underlying boundaries are **CC BY 3.0 IGO**. Retain both notices and the required attribution:

> Boundaries: NAMRIA and PSA through OCHA COD-AB (CC BY 3.0 IGO); GeoJSON: James Faeldon (MIT). Regrouped and ring orientation normalized by GasPriceApp.

See `Geography/LICENSE-MIT.txt`, `LICENSE-CC-BY-3.0-IGO.txt`, `SOURCE-README.md` and `UPSTREAM-README.md`. The CC license text was obtained from [Creative Commons](https://creativecommons.org/licenses/by/3.0/igo/legalcode). The API index and each FeatureCollection include attribution and the license URL. The future map must display attribution, preserving the basemap provider's attribution too. Do not imply endorsement.

## Coverage, geometry and identifiers

The package includes **1,642 independently selectable cities/municipalities** in **93 files**, matching every `City`/`Mun` entry in the pinned PSA-derived Q3 2026 inventory. No barangay or manually traced geometry is included. Every source medium-resolution polygon passes Shapely topology checks; holes and disconnected components remain intact. The transformation only normalizes ring orientation and regroups features, retaining the source coordinates. Medium resolution is upstream's 1% vertex-retention simplification; small coastal details reflect that source resolution.

Each feature's string `id` and `properties.cityId` is its 10-digit PSGC, with leading zeros restored. Codes are stable within this version; administrative reorganizations can change them between vintages. Requests include `boundaryVersion` to avoid silently joining incompatible vintages. New geometry or crosswalk revisions require a new package version and updated proxy allowlists; never overwrite an immutable version.

The index contains city IDs/names/levels, city classification, region, nullable administrative province ID, **geographic storage group** ID/name/type, city bounding box, interior label point and file ID. File entries contain bounds, member IDs, uncompressed size, estimated raw-GeoJSON gzip size and SHA-256. Bounds use `[west,south,east,north]`; points use `[longitude,latitude]` in WGS84. An index bounding-box intersection is a candidate test; perform exact rectangle/polygon intersection after loading a chunk.

NCR uses district storage groups, including City of Manila, rather than an invented province. Pateros is a municipality. HUC/ICC records have `provinceId: null`; their upstream geographic province grouping is not administrative membership. BARMM's Special Geographic Area uses the source's interim group code, without claiming a province. City of Manila's default ID is `1380600000`.

There are 7,178,155 raw geometry bytes (~2.24 MB gzip across all files). The largest file is 261,280 bytes. The 685,133-byte index compresses substantially; clients do not need full national polygons to find relevant files. Geography endpoints use shared envelopes, so wire sizes differ from raw-file sizes.

## Reproducing and auditing the package

Use Python with `shapely==2.0.7` (development preparation only; no runtime Python dependency). Download these immutable inputs outside the repository:

- Boundary archive: `https://codeload.github.com/faeldon/philippines-json-maps/zip/680e32e62baeccbcd8845d3b1387bc67c1baf78a`
  SHA-256: `b78f64b04e544f8485c56bafca3b9f3936e9eae48c5c0c25ab4d811b076d17b1`
- PSA-derived inventory: `https://raw.githubusercontent.com/faeldon/philippines-psgc-shapefiles/be65ee15b10bba45e310f6edeec1bd45afcb33b6/data/psgc/2026-3q/psgc.csv`
  SHA-256: `83cbac96063b9227994b5fd17f791a3ac9ddac76427b3b01c3fe5a2a9f2f796b`
- Upstream provenance README: `https://raw.githubusercontent.com/faeldon/philippines-psgc-shapefiles/be65ee15b10bba45e310f6edeec1bd45afcb33b6/README.md`
  SHA-256: `f1766dbb647fbee08949996a04f3efe4ffbc474d8e3e63f7b806b3213465caac`

```sh
python GasPriceBackend/scripts/prepare_map_boundaries.py --archive /tmp/boundaries.zip --psgc /tmp/psgc.csv --output /tmp/rebuilt-map
```

Compare rebuilt files to `Geography/ph-2026q3-v1/`. The script verifies input hashes, inventory equality, unique IDs, nonempty valid polygons, Philippine extent and interior label anchors. `manifest.json` records original file hashes and the output index checksum; the backend verifies index and geometry hashes at startup. MIT and upstream README notices are copied beside the output version directory; the archived official CC terms and pinned upstream provenance README are separately preserved in `Geography/`.

## Geographic price crosswalk

Existing DOE records remain name-keyed; no schema rewrite occurs. `FuelMapGeography.Resolve` uses the entire versioned catalog, canonical city names (including `City of …`/`… City`, case/diacritics), and canonical province aliases from the existing matcher. It never matches a city globally by name alone. Catalog lookups must resolve uniquely; ambiguous/unmatched names return no city. Legacy `CityKey`-only records are supported. NCR's DOE province aliases map to Metro Manila; independent cities can match DOE's geographic province without gaining administrative membership. Pre-split `Maguindanao` is an explicit additional matching alias for municipalities grouped under its two successor provinces. Existing region/province consistency checks still apply.

This is an auditable deterministic crosswalk, not fuzzy geocoding. Report spellings not covered by canonical rules remain unavailable until reviewed. Production mapping coverage cannot be established without auditing actual imported records; no production database was queried for this implementation.

## Endpoints

### Public geography reads

- `GET /fuel-prices/map/geography/index`
- `GET /fuel-prices/map/geography/ph-2026q3-v1/{fileId}`

Both return the existing `ApiResponse<T>` envelope. Index `data` is the geographic index; file `data` is a GeoJSON FeatureCollection. No query parameters are accepted. File IDs come from the index, e.g. `1303900000-01`; they are dictionary lookups, never filesystem paths. Unknown files/versions return enveloped 404; invalid queries return enveloped 400, without caching errors. Package SHA-256 values refer to raw GeoJSON files; HTTP ETags refer to the selected enveloped representation.

Index caching: `public, max-age=300`. Versioned files: `public, max-age=31536000, immutable`. Both support gzip, `Vary: Accept-Encoding`, representation-specific ETags and conditional GET/304. Asset envelope `date` is the fixed source build date, preserving content across process restarts; it is not a price timestamp. Geography is intentionally public and carries no user state. Geography cache metadata is preserved through both proxies; authenticated prices remain private/no-store. Proxy origin fetches still bypass edge caching; browser/device HTTP caches can reuse geography.

Rate limits per client IP: index one-second cooldown; all file IDs/versions share **120 requests/minute**, permitting parallel chunk loading. Batch prices have a **750 ms cooldown**. Responses include the existing 429 envelope and `Retry-After`. Failed requests consume permits.

### Authenticated batch prices

`POST /fuel-prices/map/prices`

```json
{"boundaryVersion":"ph-2026q3-v1","cityIds":["1380600000","0730600000"]}
```

Use existing native bearer + `X-App-Instance-Id`, or browser cookie auth + valid CSRF token. Browser clients should use the existing authenticated request mechanism. No query parameters, unknown/duplicate properties, numeric codes, unrecognized codes, nulls, malformed JSON or non-JSON media types are accepted. Body limit is **32,768 bytes**, including unknown-length transfer bodies. Accept **1–2,000 input IDs**, deduplicated preserving first-request order. The full current national inventory fits in one request. The response uses `ApiResults.Ok`/the existing envelope; no existing endpoint contract changes.

`data` includes `boundaryVersion`, `gasolineGrade: "RON 91"`, `dieselGrade: "DIESEL"`, `generatedAtUtc`, `expiresAtUtc`, `freshnessPolicy`, and `items`. Each item includes `cityId`, `scope: "city"`, `status` (`available`, `partial`, `unavailable`), nullable `gasoline`/`diesel`, report week, consistent monitoring dates if available, source URLs, latest fetched time and item expiry. Ranges contain decimal `minPrice`, `maxPrice`, `currency: "PHP"`, `unit: "liter"`.

**RON 91 was explicitly confirmed for gasoline.** Never substitute RON 95/97/100 or merge them. Ordinary DIESEL excludes DIESEL PLUS. Province/region rows are never spread across city polygons. `scope: "city"` identifies the requested/result granularity; an unavailable result asserts no price for that city. Unavailable results have null ranges and null report/source dates, not zero prices. Unknown city IDs reject the whole request with 400; known cities without prices succeed with an unavailable item.

One asynchronous, no-tracking database query reads eligible DOE rows for the requested geographic province keys and two exact grades. In-memory deterministic crosswalking/grouping follows; there are no per-city queries, imports, AI calls, network downloads or geocoding calls. Normal authentication also performs its existing guest lookup. Existing DOE indexes are reused.

## Price freshness and aggregation policy

Policy identifier: `doe-week-end-within-7-days-v1`.

- Determine today's calendar date in Asia/Manila, with UTC API timestamps.
- Accept stored reports whose start is not in the future and end is at least today minus seven days; reject inverted coverage or coverage longer than seven days.
- Reject future fetch timestamps, invalid/nonpositive ranges, blank company names and non-HTTPS/non-DOE source URLs.
- Match exact city coverage first. A newer provincial report never becomes a city value.
- Select one report identity (week start/end, source URL and report ID), newest week first, then week end, latest fetch and deterministic URL tie-break. Do not fill a missing grade from another report/week.
- Within that report, keep the latest observation per company/grade. Return minimum company minimum and maximum company maximum, separately per grade.
- Return `partial` when only one grade exists and `unavailable` when neither exists. Missing grades never borrow values.
- Cache validity is **five minutes**, including unavailable results, capped by the Philippine midnight at which the report becomes ineligible. The feed expires when its earliest item expires. This is a refresh interval, not a claim that weekly prices were observed five minutes ago.
- HTTP prices are `no-store`; authorized future clients may use the explicit per-item application cache expiry. No server price cache delays newly imported rows.

## Verification

```sh
dotnet build GasPriceApp.sln
dotnet run --project GasPriceBackend/tests/FuelMapChecks/FuelMapChecks.csproj
node --test CloudflareWeb/test/worker.test.js CloudflareWorker/test/worker.test.js
dotnet publish GasPriceBackend/BackendServer.csproj --no-restore -o /tmp/gasprice-map-publish
```

Focused checks host the actual map endpoints with isolated EF InMemory fixtures and no external clients. They exercise batch reads, authentication, CSRF rejection, input bounds, missing data, report/grade separation, aliases, expiry, asset caching and real middleware rate limits. They do not substitute for a PostgreSQL integration run or a production DOE spelling audit. The geometry pipeline separately validates every polygon against the pinned inventory.
