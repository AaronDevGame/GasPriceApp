# DOE fuel adjustments

## Externally extracted notice JSON

`POST /admin/fuel-prices/adjustments/import/extracted` accepts a notice already extracted by an external agent. Like `/admin/fuel-prices/doe/import/extracted`, it requires `Authorization: Bearer <CODEX_ADMIN_API_KEY>`; the general admin key and news key are rejected. Configure that dedicated key on the server and future scheduled-task environment. A missing or blank key disables this route. The original PDF-import/backfill endpoints continue to require `ADMIN_API_KEY`.

Send `Content-Type: application/json`, no query parameters, and the following camelCase body. All fields shown are required, including nullable fields; explicitly send `null` when the PDF does not state a product or effective time.

```json
{
  "sourceUrl": "https://doe.gov.ph/example-adjustments.pdf",
  "weekStart": "2026-10-27",
  "weekEnd": "2026-11-02",
  "rows": [
    {
      "oilCompany": "PETRON",
      "effectiveDatePhilippines": "2026-10-27",
      "effectiveTimePhilippines": "06:00:00",
      "gasolineChangePerLiter": -1.25,
      "dieselChangePerLiter": 0.50,
      "keroseneChangePerLiter": null
    }
  ]
}
```

- `sourceUrl` must be a direct HTTPS PDF URL on the existing DOE host allowlist (including the approved DOE CloudFront host), at most 2048 characters. The server validates the URL but does not download or verify the PDF. The submitting agent must verify provenance and extracted figures against it.
- The notice week must run Tuesday through Monday, using `yyyy-MM-dd`. Each effective date must lie within that week. A stated Philippine time must use `HH:mm:ss`; it is converted to UTC. A `null` time preserves the Philippine date and stores a null UTC timestamp.
- Submit 1–100 company rows. Trimmed company names must contain 1–100 characters. Duplicate company/date/time combinations, including case/whitespace variations, are rejected. Staged adjustments at distinct effective dates/times are separate rows.
- At least one product amount per row must be present. Amounts are signed PHP/liter numbers from -100 to 100 with at most two decimal places. Zero means no change; `null` means not listed. Numeric strings, unknown/missing fields, duplicate JSON properties, malformed JSON and null rows are rejected.
- The body limit is 128 KiB, including chunked requests. Invalid submissions return `400` in the shared API envelope before any changes are saved. Authentication failures return `401`; the shared admin/IP one-second cooldown returns `429` with `Retry-After`.

Unlike the queued DOE report import, this bounded import saves synchronously and returns HTTP `200` after `SaveChangesAsync`, without a job or polling URL. It makes no PDF downloads or OpenAI calls and requires no `OPENAI_API_KEY`. It uses the existing `fuel_adjustment` table, validation and upsert behavior. A row matches by notice week, case-insensitive company, effective date and optional UTC time. Omitted rows are preserved; there is no report-completeness or deletion flag.

The response uses the normal `ApiResponse<T>` envelope, with `message` equal to `fuel_adjustments_imported`, `fuel_adjustments_updated` or `fuel_adjustments_already_imported`. Its `data` contains `weekStart`, `weekEnd`, `sourceUrl`, `added`, `updated`, `fetchedAtUtc` and `status`. Repeating the same notice does not duplicate rows or refresh unchanged row timestamps; corrections update matching rows. Existing source-URL equivalence rules also apply. Changes to a company's effective date/time produce a new row rather than removing the previous row.

No schedule is enabled by adding this endpoint. The future automation can extract the PDF, submit this JSON and inspect the synchronous response.

## Existing feed and server-side PDF imports

The existing `GET /fuel-prices` returns retail pump-price estimates. Fuel adjustments are a separate, nationwide DOE notice feed. A negative change means a decrease in PHP per liter; a positive change means an increase. `null` means the notice does not list that product for the company.

`GET /fuel-prices/adjustments?weeks=5` returns company-level adjustments from up to five latest imported Tuesday-to-Monday notice weeks. It uses the same guest bearer token and `X-App-Instance-Id` as the other fuel-price read endpoints, or the established browser cookie. `weeks` is a maximum number of distinct imported weeks, not a calendar date range. It accepts 1 through 52 and defaults to 3. For example, five imported weeks from September 1 through October 1 return `weeks: 5` even if a caller requests `?weeks=52`; `requestedWeeks` reports the requested limit. The response's `groups` array contains one object per week with `weekStart`, `weekEnd`, and sibling company rows in `adjustments`. An empty `groups` array with `weeks: 0` means no notices have been imported. The endpoint only reads PostgreSQL. Each adjustment includes `sourceUrl`, `fetchedAtUtc`, and `effectiveDatePhilippines`. `effectiveAtUtc` is null when DOE states a date but no time; this is not a current pump-price feed.

`POST /admin/fuel-prices/adjustments/import` requires `Authorization: Bearer <ADMIN_API_KEY>`. With no query parameter, it looks for the first PDF link on DOE's [Price Adjustments page](https://doe.gov.ph/data-and-prices/liquid-fuels/retail-pump-prices/price-adjustments), which DOE currently orders newest first. For a specific historical notice, pass a URL-encoded `sourceUrl` query parameter pointing to a DOE PDF. Only HTTPS links on DOE's approved hosts or its current attachment CDN are accepted. This endpoint takes no request body.

For a backfill, call `POST /admin/fuel-prices/adjustments/import?from=2026-08-28&to=2026-09-09`. Both dates are inclusive `yyyy-MM-dd` dates, in the Philippines notice calendar, with a maximum span of 366 days. **Every notice whose Tuesday-to-Monday week overlaps the requested dates qualifies**, even if only one day overlaps. The server discovers PDFs linked on DOE's current Price Adjustments page and filters clearly dated filenames before counting notices. Ambiguous filenames still undergo PDF extraction; the extracted week is the final authority before saving. For notices no longer listed there, send an optional JSON body such as `{"sourceUrls":["https://doe.gov.ph/path/to/notice.pdf"]}`; these URLs must pass the same DOE-host validation. A backfill is capped at 60 distinct PDFs, including discovered and supplied URLs. Only one backfill runs per server instance at a time.

The backfill returns HTTP `202` with a `jobId`. Poll `GET /admin/fuel-prices/adjustments/import/jobs/{jobId}` with the admin bearer token. Status includes `totalNotices`, per-PDF outcomes (`imported`, `updated`, `already_imported`, `outside_range`, or `failed`), counts on imported notices, and `missingWeeks` containing Tuesday dates for requested weeks that have no saved rows. A PDF is checked against the **week extracted from the PDF** before saving. Already imported source URLs are skipped, so the single-notice import route remains the way to refresh a changed PDF. Failures are isolated per PDF. Job status lives in server memory and is lost when the server restarts; run the backfill again after a restart if necessary. DOE's current listing may not cover older months, so provide their PDF URLs when missing weeks remain.

The backend downloads the PDF, sends its bytes to the configured OpenAI model using `agents/philippines-fuel-adjustment-extractor.md`, validates the dates, company names, amounts, and any stated per-company effective times, then inserts or updates matching rows. A row is matched by week, company, effective date, and optional time, so importing the same notice again does not add duplicates. The single-notice response has a `status` of `imported` for a new notice, `updated` when saved rows change or rows are added to an existing notice, or `already_imported` when all extracted rows for that PDF match the saved rows. PDF source URLs with a literal slash or `%2F` in the query are treated as equivalent. The response `message` is `fuel_adjustments_` followed by that status. `added` counts new rows; `updated` counts existing rows whose values, week end, or source URL changed. Unchanged rows keep their saved `fetchedAtUtc`; an `already_imported` response has `added: 0` and `updated: 0` and leaves the database untouched. Existing rows absent from a later PDF extraction are not deleted. A company that stages changes over two dates receives two rows. The importer rejects an empty or structurally invalid extraction without saving rows. Numeric and structural validation cannot guarantee that AI copied every figure correctly; check the source PDF before relying on an imported week. `fetched_at_utc` records the time each row was saved.

This endpoint is a manual trigger. No recurring scheduler is configured; call it for each new DOE notice until a deployment scheduler is added. The read endpoint never invokes the AI extractor. Set `OPENAI_API_KEY` to enable imports. The PDF itself is not stored in PostgreSQL.
