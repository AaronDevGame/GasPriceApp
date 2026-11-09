# Fuel buying advice

`GET /fuel-advice?fuelType=gasoline` returns price-based buying advice in the usual
`ApiResponse<T>` envelope. This public read needs no login, makes no AI or upstream
calls, and never uses tank level or remaining range. It is available through both
Cloudflare proxies after deploying their route allowlist changes.

`fuelType` is required: `gasoline`, `diesel`, or `kerosene`. Optional `oilCompany`
is an exact, case-insensitive company name (2–100 plain-text characters, without
surrounding whitespace). Unknown, repeated, empty, or invalid parameters return
HTTP 400. Missing data or unknown companies return HTTP 200 with `unknown`.
Requests share a one-second cooldown per client IP regardless of query parameters;
HTTP 429 includes `Retry-After`. Responses use `Cache-Control: no-store`.

## Data and publishing

Reuse the existing `fuel_adjustments` table for confirmed DOE company adjustments,
and structured `FuelNewsContent.Adjustments` in news JSON for forecast/confirmed
announcements. No new table or migration is required. Neither headlines nor body
text are parsed at request time. Existing news publishing and DOE imports remain
the write paths; this endpoint does not publish or import anything.

Without `oilCompany`, only news rows with a null company apply. With a company,
matching DOE rows and matching or nationwide news rows apply. Company-specific
announcements are never treated as nationwide. Amounts are signed PHP per liter:
negative for rollback, positive for increase, and zero for unchanged. A null DOE
fuel amount is missing data, not a zero change. Different fuel types are evaluated
independently.

Only unexpired, unsuperseded adjustment news qualifies. Both the article and its
row must be confirmed to produce confirmed advice. Forecast evidence is exposed
separately and cannot produce `gas_now` or `wait`. An upcoming forecast may be
shown alongside advice backed by a separate confirmed adjustment.

## Timing and decision rules

Evaluate at the server's current UTC time using `TimeProvider`. Philippine dates
use UTC+08:00. Consider adjustments from the previous seven Philippine dates
through the next seven dates, and reject future events beyond seven days from now.
An adjustment expires seven days after its effective timestamp, or seven days
after the start of its Philippine date when no exact time is supplied. Earlier
news expiry still wins. Forecast context also expires at its effective timestamp
or the end of its Philippine date.

The nearest upcoming confirmed date controls the advice. If none is upcoming,
use the latest applied confirmed date. A date-only announcement stays pending
through its Philippine date. New confirmed events replace the previous event's
advice even when the previous event was a rollback.
An expired newer event blocks reviving an older recommendation. Recent expired
news is read only to detect this condition; it cannot itself produce advice.

| Confirmed adjustment | Before it takes effect | At/after it takes effect |
| --- | --- | --- |
| Negative | `wait` | `gas_now` |
| Positive | `gas_now` | `neutral` |
| Zero | `neutral` | `neutral` |

For date-only notices, before the Philippine date use the upcoming rule. During
that date return `unknown` with `effective_time_unknown`; after the date ends use
the applied rule. Never invent a 06:00 effective time. Conflicting amounts or
different exact times for the selected date return `unknown` with
`conflicting_adjustments`. All applicable sources for that date are included so
the caller can inspect the disagreement. Forecast-only and absent/stale data
return `unknown`, with `forecast_only` or `no_confirmed_adjustment` respectively.

## Response data

Example fixture: a confirmed gasoline rollback effective at November 10, 2026,
06:00 Philippine time. The amount and source below are illustrative.

```json
{
  "fuelType": "gasoline",
  "oilCompany": null,
  "status": "gas_now",
  "reason": "rollback_effective",
  "message": "The confirmed rollback is now effective. Good time to refuel.",
  "evaluatedAtUtc": "2026-11-09T22:00:00Z",
  "validUntilUtc": "2026-11-16T22:00:00Z",
  "evidence": [{
    "confirmationStatus": "confirmed",
    "effectiveDatePhilippines": "2026-11-10",
    "effectiveAtUtc": "2026-11-09T22:00:00Z",
    "minChangePerLiter": -2,
    "maxChangePerLiter": -2,
    "oilCompany": null,
    "newsId": "11111111-1111-1111-1111-111111111111",
    "adjustmentId": null,
    "sourceUrls": ["https://example.com/fixture"],
    "validUntilUtc": "2026-11-16T22:00:00Z"
  }],
  "forecast": null
}
```

Render green for `gas_now`, amber for `wait`, and gray for `neutral`/`unknown`.
Use the machine-readable status/reason for presentation and the message for
explanation. Refetch at `validUntilUtc`, on app resume, and when the selection
changes. Earlier imports or corrections can change advice before that timestamp.
The nullable `forecast` contains one upcoming forecast for context, never a
guarantee. There can be several confirmed evidence entries; each links to either
a news article or an existing DOE adjustment. All returned timestamps are UTC.

Frontend UI integration is separate: when adding a caller using the Expo shared
transport, include `/fuel-advice` in its API path allowlist.

## Verification

Run `dotnet build GasPriceApp.sln` and
`dotnet run --project GasPriceBackend/tests/FuelNewsChecks -- --advice-only` for
deterministic decision checks. `python3 GasPriceBackend/tests/fuel_news_smoke.py`
also exercises the HTTP endpoint against a disposable PostgreSQL database when
local PostgreSQL binaries are installed. The screenshot's forecast is not seeded
as confirmed data by this feature.
