# Import DOE prices extracted in a chat

`POST /admin/fuel-prices/doe/import/extracted` accepts company prices already extracted from a DOE PDF. It makes **no OpenAI API calls and no PDF downloads**, and works without `OPENAI_API_KEY`. The chat reads the PDF using its available tools; the backend validates and saves submitted data. Chat subscription usage does not become backend API credit.

This endpoint uses `Authorization: Bearer <CODEX_ADMIN_API_KEY>` and `Content-Type: application/json`. The general admin key, news key and existing news-only MCP OAuth connection do not authorize it. Missing or blank `CODEX_ADMIN_API_KEY` fails closed. Keep credentials in a trusted connection or local environment, never in chat prompts, committed files or scheduled-task text. Deploy the backend and configure the matching shared secret in Render and the task runtime before enabling the schedule. No Auth0 configuration is required for this REST workflow.

## Request

Submit one PDF and its printed coverage period per request. The following is **illustrative data only**, not extracted prices to publish:

```json
{
  "sourceUrl": "https://doe.gov.ph/example-report.pdf",
  "weekStart": "2026-10-27",
  "weekEnd": "2026-11-02",
  "coverageSource": "report",
  "complete": true,
  "rows": [
    {
      "city": "Quezon City",
      "province": "Metro Manila",
      "region": "National Capital Region",
      "oilCompany": "PETRON",
      "fuelGrade": "DIESEL",
      "minPricePerLiter": 60.00,
      "maxPricePerLiter": 65.00,
      "monitoringStart": null,
      "monitoringEnd": null
    }
  ]
}
```

- Required top-level fields: `sourceUrl`, `weekStart`, `weekEnd`, `complete`, `rows`. `coverageSource` defaults to `report`; use `monitoring` only when a clearly report-wide printed monitoring period supplies coverage and no explicit report period exists. Dates use `yyyy-MM-dd`.
- Required row fields: `province`, `oilCompany`, `fuelGrade`, `minPricePerLiter`, `maxPricePerLiter`. `city` may be omitted, null, empty or whitespace for a clearly identified province-level block; it is normalized to an empty city/key in storage. Never invent a city for a province-only table. `region`, `monitoringStart` and `monitoringEnd` may be omitted or null. Monitoring dates apply to that row's original page, never inherited from another page. Supplying null removes existing monitoring metadata when that price is updated.
- Source must be a direct HTTPS PDF URL on the existing DOE PDF host allowlist, including the DOE CloudFront host. The retail-price listing URL itself is not a PDF. The backend validates the URL's shape/host but does not download it or verify the extraction against the PDF; the authenticated submitting agent must verify provenance, dates, column alignment and values.
- Coverage spans at most eight inclusive days; its start cannot exceed tomorrow in Philippine time. Monitoring dates require both endpoints or neither, in order, ending no later than tomorrow in Philippine time. Printed PDF coverage takes precedence over listing labels. Do not invent missing coverage.
- Include 1–4000 rows, at most 2 MiB of JSON per request. Locality/company/region strings have the existing 100-character limits. Supported grades are `RON 100`, `RON 97`, `RON 95`, `RON 91`, `DIESEL`, `DIESEL PLUS`, `KEROSENE`.
- Prices must be JSON numbers from PHP 1–300, with at most two decimal places and minimum no greater than maximum. For one printed price, submit that value as both endpoints. No inferred, rounded or estimated prices.
- Submit named company prices only. Omit independent, common-price and overall aggregates and blank cells. Identical duplicate cells are counted and deduplicated; conflicting duplicates, including monitoring dates, reject the whole request.
- Unknown or repeated JSON properties, missing required values, malformed JSON, null rows, wrong types and invalid rows return HTTP `400` before queueing anything. Revalidate persisted payloads before writing prices. Invalid submissions never partially write prices.
- `complete: true` is the submitter's assertion that every page and eligible named-company cell in that PDF was read and accounted for. Use `false` for a partial extraction; it saves valid submitted rows but leaves the report partial and the job `completed_with_gaps`. Never assert completeness for an unreadable, truncated, or page-batched submission. Province-only company prices count toward completeness. A readable section with no eligible company prices (blank/No LFRO/zero placeholders, for example Romblon) adds no rows and does not itself make the report incomplete.

## Queue, verify and retry

The POST returns HTTP `202` with the standard `ApiResponse` envelope, `data.mode: "extracted"`, `data.jobId`, `data.statusUrl`, and a matching `Location` header. Acceptance means the request is queued, not that prices have been saved. No change to the older API-backed import endpoints is required.

Poll the returned `GET /admin/fuel-prices/doe/import/extracted/jobs/{jobId}` URL with the same Codex authorization every 2–5 seconds until `completed`, `completed_with_gaps`, `failed` or `interrupted`. Both endpoints retain the shared one-second admin cooldown and return `401`/`429` through the existing middleware. Follow `Retry-After` when rate limited.

The shared durable worker slot serializes external submissions with other DOE imports across hosts. Requests survive client disconnects and server restarts; an interrupted job requires a new POST. Polling returns the source URL summary, progress/status and final result, without echoing the submitted row array. Final `data.result` includes `added`, `updated`, `priceRows`, `status`, coverage dates and duplicate counts. Its `model` is `external:submitted`; usage/cost are null because no backend AI request ran.

Prices are upserted by report week, canonical locality, company and fuel grade (an empty city identifies the province-level range, separately from every named city) using the existing importer. Repeating unchanged data creates no duplicate price rows and preserves price timestamps; a complete repeated import reports `already_imported`. Corrections update matching prices. Repeated POSTs can create separate job records, so poll a known job before resubmitting after a timeout. Omitted rows are retained, never deleted. Report counts and affected research-cache expiration are saved in the same transaction as prices. The backend records no fabricated PDF content hash or extraction page cache.

## Daily scheduled task and separate environments

Use `CODEX_ADMIN_API_KEY` as an application-owned shared secret. It is not an OpenAI subscription key or OpenAI API credential. It grants access under `/admin/fuel-prices/doe/import/extracted` and to the [externally extracted fuel-adjustment import](fuel-adjustments.md#externally-extracted-notice-json) at `/admin/fuel-prices/adjustments/import/extracted`; other admin operations retain their existing credentials. The dedicated job route returns only `mode: extracted` jobs. Both new reads are in `RouteRegistry.Admin` and retain the existing shared admin rate limit; wait at least two seconds between API requests and respect `Retry-After`.

1. Generate a long random secret (for example, `openssl rand -hex 32`). Set it as `CODEX_ADMIN_API_KEY` in the existing Render backend environment using `.env.doe-render.example` as a template. Keep the backend's other required settings, including `ADMIN_API_KEY`. Deploy this change.
2. Copy `GasPriceBackend/.env.doe-task.example` to `.env.doe-task` in the repository root. Set its `CODEX_ADMIN_API_KEY` to the same value and verify `DOE_API_BASE_URL`. Restrict the private file with `chmod 600 .env.doe-task`. The file is gitignored. Never paste its contents into chat or automation text.
3. The helper loads this file directly without executing shell code. Runtime environment variables take precedence. For a different task environment, provision those two variables there or pass `--env-file /private/path/to/file`. Render uses its configured environment variables; ASP.NET does not automatically load these example dotenv files. A local file does not automatically transfer to ChatGPT/cloud environments.
4. Enable the daily 09:00 Asia/Manila automation after deployment and configuration. A local desktop automation needs the computer and app available; a cloud task needs its own configured environment, network access, and HTTP/PDF tools. The news MCP connector does not expose these DOE routes.

The scheduled agent follows `GasPriceBackend/agents/doe-scheduled-import.md`. It discovers the latest PDF per section/subdivision, verifies printed coverage, checks the DB, extracts every page only for missing/incomplete reports, submits a complete PDF at a time, polls and reports results. A newer period in one section must not hide a missing report in another section.

### Check the database before extraction

```bash
python3 GasPriceBackend/scripts/doe_task.py check 'https://doe.gov.ph/actual-report.pdf'
```

This calls `GET /admin/fuel-prices/doe/import/extracted/reports?sourceUrl=<encoded-PDF-URL>`. The standard response `data.reports` lists exact source matches with coverage, status, row count and import timestamp. Stop for that report only when its printed coverage matches a saved report with `status: imported` and `priceRows > 0`. A partial record or a different coverage period is eligible for extraction. If every latest section report is already complete, report “Records already exist” and end that run; keep the recurring schedule enabled. DOE can replace a PDF at the same URL: this check follows source URL and coverage, not binary revision detection; explicit corrections with unchanged identity require manual review/reimport.

### Submit and verify

```bash
python3 GasPriceBackend/scripts/doe_task.py submit /private/path/report.json --receipt /private/path/new-job-receipt.json
# Resume polling an accepted job after a timeout:
python3 GasPriceBackend/scripts/doe_task.py poll JOB_UUID
```

Use a private persistent task directory for payloads and receipts so later runs can resume pending jobs. The helper reserves a receipt before sending, saves acceptance before polling, rejects reuse of an existing receipt, refuses HTTP redirects, and never logs the secret. If a receipt says `submission_pending`, the POST outcome is uncertain: check saved reports before any retry. A successful POST alone is not success. The helper polls for up to ten minutes and exits unsuccessfully for gaps, failures, interruption or timeout; continue polling known jobs instead of blindly resubmitting. Confirm the saved report after completion. This is an HTTP helper; the scheduled agent performs the PDF extraction with its tools. It invokes no OpenAI API itself.

## Checks

```bash
dotnet build GasPriceApp.sln
dotnet run --project GasPriceBackend/tests/DoeExtractedImportChecks
# Optional real database/worker checks against a disposable localhost cluster only:
DOE_TEST_PGPORT=55432 dotnet run --project GasPriceBackend/tests/DoeExtractedImportChecks
```

Offline checks run a local HTTP server with captured job writes, the real auth/rate-limit middleware and no network clients. Optional PostgreSQL checks use only database `gasprice_doe_submitted_test`, apply migrations, and clear its import/price/report/research-cache tables. Never point them at production.
