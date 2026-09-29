# Fuel-price feed and history

Both read endpoints require `Authorization: Bearer <token>` and
`X-App-Instance-Id`. They only read stored estimates. Use the existing
`POST /ai/fuel-prices` with coordinates to research a local price before
reloading the feed. A failed research request does not affect the general feed.

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

## Feed

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

History is based on previous successful AI research and can be sparse. It
does not generate new prices or run a background collection job.
