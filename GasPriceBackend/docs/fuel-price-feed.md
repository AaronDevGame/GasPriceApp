# Fuel-price feed and history

Both read endpoints require `Authorization: Bearer <token>` and
`X-App-Instance-Id`. They only read stored estimates. Use the existing
`POST /ai/fuel-prices` with coordinates to research a local price before
reloading the feed. The backend resolves the coordinates, checks its fresh
cache, then reads DOE's latest matching regional pump-price PDF. DOE company
prices and ranges are saved separately by city, fuel grade, and report week,
and are included in a successful DOE research response as `doePrices`.
The PDF does not identify individual station locations. If DOE has no fresh
matching rows or cannot be read, the backend checks enabled direct sources in
`agents/fuel-price-sources.json`.
It accepts only city-matched PHP-per-liter prices with an exact date within
seven days. When both direct sources qualify, the later verified date wins;
the configured priority breaks a date tie. If neither qualifies, the existing
AI web search runs. Direct results use the same cache and response shape;
`model` begins with `direct:` and AI usage and cost are null. A failed research
request does not affect the general feed.

`fromCache` means this request reused a saved research result. `cacheStored`
means this request saved a new research result; it is `false` on a cache hit.
`cacheScope`, `cachedAt`, and `refreshAfter` still describe the reused cache
entry on a hit.

For AI fallback, the backend adds the enabled direct-source domains it just
checked to the web-search blocked domains and tells the agent not to cite them.
They remain in `fuel-price-sources.json`, separate from the permanently excluded
sources file. The backend rejects a new AI result that cites a directly checked
domain.

Each configured source needs a matching backend parser. Adding an entry to the
JSON file alone cannot make a new website readable. Origins are restricted to
the matching source domain, and the configuration is loaded at startup. The
backend reads public city pages without using MetroFuel's `/api/` paths.

Zigwheels fuel-price evidence is excluded. Stored snapshots citing it are
omitted from research cache hits, the feed, and history, including snapshots
that also cite other sources. Research can reuse the next allowed fresh cache
entry or search again. New AI results citing an excluded source receive HTTP
502 (`ai_invalid_response`) and are not cached. No cache deletion or database
migration is required.

Maintain excluded names and domains in `agents/excluded-fuel-price-sources.json`.
The backend loads and validates this file at startup, uses it for all source
checks, and appends its contents to the fuel-price agent's Markdown instructions.
Domain exclusions include subdomains. Restart or redeploy after editing the file;
a missing or invalid file prevents startup rather than disabling exclusions.
The AI search also blocks listed domains directly and uses a small search context.
Research is limited to two web tool actions and searches allowed source tiers
without giving DOE the first search.
An `unavailable` result is not reused, including results cached by earlier
releases. This lets the next request research again after a source outage.
The feed and history omit unavailable snapshots.

## Feed

`GET /fuel-prices/featured` returns `data.groups` in Luzon, Visayas, Mindanao
order. Each group has `name` and `items`, with up to three cities in the
published candidate order. A card contains `city`, `province`, `reportWeekStart`,
`reportWeekEnd`, `dataAsOf`, `prices` (diesel, gasoline91, gasoline95 PHP-per-liter
ranges), and a DOE `source` with a report URL. Missing grades have null range
endpoints. A group with no qualifying city has an empty `items` array. The
endpoint takes no query parameters and uses the same player authentication as
the existing feed. It reads only `doe_fuel_price`; it does not import, research,
geocode, or write to `fuel_price_cache`.

Candidate cities are Quezon City, Baguio City, and Dasmariñas for Luzon; Iloilo
City, Cebu City, and Tacloban City for Visayas; and Zamboanga City, Cagayan de
Oro City, and Davao City for Mindanao. The latest city report qualifies when its
week overlaps today and its end date is within the last seven Philippine calendar
days. The displayed `dataAsOf` is the report's end date for completed weeks or
its start date for the current week. A city without a qualifying stored report
is omitted rather than filled from another source.

`GET /fuel-prices?limit=10` returns up to ten distinct areas, with each
area's latest `dataAsOf` snapshot. The default limit is 10; accepted values
are 1 through 10. If the database has fewer areas, `items` is shorter or empty.
Older stored estimates remain visible with `freshness: "stale"`; estimates
observed within the last seven days are `"current"`.

After `POST /ai/fuel-prices` succeeds, pass the `result.location.city`,
`province`, and `region` values as optional query parameters:

```text
GET /fuel-prices?limit=10&city=Quezon%20City&province=Metro%20Manila&region=National%20Capital%20Region
```

The feed puts a current matching city estimate first, otherwise a current
province, then a current region. If no current match exists, it tries those
same levels among stale estimates. Remaining areas follow in descending
`dataAsOf` order. `localAreaStatus` is `not_provided`, `available`, or
`not_cached`. Request failures use HTTP errors, not this status field.
`area.level` reports the geographic level actually supported by the price
evidence. `prices` holds PHP-per-liter ranges for diesel, gasoline 91, and
gasoline 95; unavailable fuel types have null minimum and maximum values.

## History

`GET /fuel-prices/history?scope=city&city=Quezon%20City&province=Metro%20Manila`
returns dated snapshots for exactly one area, newest first. `scope` is
`city`, `province`, or `region`. A city requires its name and a province or
region; a province requires its name; a region requires its name. The default
limit is 30; accepted values are 1 through 100. Duplicate observations with
the same `dataAsOf` are collapsed to the latest cached snapshot. When no
snapshot exists, `items` is empty.

History is based on previous successful research and can be sparse. It
does not generate new prices or run a background collection job.
