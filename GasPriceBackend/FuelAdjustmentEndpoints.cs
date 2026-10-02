using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

public sealed record FuelAdjustmentItem(
    long Id,
    DateOnly WeekStart,
    DateOnly WeekEnd,
    string OilCompany,
    DateOnly EffectiveDatePhilippines,
    DateTime? EffectiveAtUtc,
    decimal? GasolineChangePerLiter,
    decimal? DieselChangePerLiter,
    decimal? KeroseneChangePerLiter,
    string SourceUrl,
    DateTime FetchedAtUtc);

public sealed record FuelAdjustmentWeek(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    IReadOnlyList<FuelAdjustmentItem> Adjustments);

public sealed record FuelAdjustmentFeed(
    int Weeks,
    int RequestedWeeks,
    IReadOnlyList<FuelAdjustmentWeek> Groups);
public sealed record FuelAdjustmentImportResult(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    string SourceUrl,
    int Added,
    int Updated,
    DateTime FetchedAtUtc,
    string Status);

public sealed class FuelAdjustmentExtractionException(string message) : Exception(message);

public static class FuelAdjustmentEndpoints
{
    public static void MapFuelAdjustmentEndpoints(
        this WebApplication app,
        AuthService authService,
        string instanceId)
    {
        app.MapGet(ApiRoutes.FuelPriceAdjustments, async (
            HttpRequest request,
            AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var auth = await PlayerAuthentication.AuthenticateAsync(request, db, authService);
            if (!auth.IsValid || auth.Guest is null)
                return auth.IsBadRequest
                    ? ApiResults.BadRequest(auth.Error, instanceId)
                    : ApiResults.Unauthorized(auth.Error, instanceId);

            var weeks = 3;
            foreach (var parameter in request.Query)
            {
                if (parameter.Key != "weeks" || parameter.Value.Count != 1 ||
                    !int.TryParse(parameter.Value[0], NumberStyles.None,
                        CultureInfo.InvariantCulture, out weeks) || weeks is < 1 or > 52)
                    return ApiResults.BadRequest("Only weeks=1..52 is accepted.", instanceId);
            }

            var starts = await db.FuelAdjustments.AsNoTracking()
                .Select(a => a.WeekStart)
                .Distinct()
                .OrderByDescending(start => start)
                .Take(weeks)
                .ToListAsync(cancellationToken);
            if (starts.Count == 0)
                return ApiResults.Ok(new FuelAdjustmentFeed(0, weeks, []), "fuel_price_adjustments", instanceId);

            var records = await db.FuelAdjustments.AsNoTracking()
                .Where(a => starts.Contains(a.WeekStart))
                .OrderByDescending(a => a.WeekStart)
                .ThenBy(a => a.OilCompany)
                .ThenBy(a => a.EffectiveDatePhilippines)
                .ThenBy(a => a.EffectiveAtUtc)
                .Select(a => new FuelAdjustmentItem(
                    a.Id, a.WeekStart, a.WeekEnd, a.OilCompany,
                    a.EffectiveDatePhilippines, a.EffectiveAtUtc,
                    a.GasolineChangePerLiter, a.DieselChangePerLiter,
                    a.KeroseneChangePerLiter, a.SourceUrl, a.FetchedAtUtc))
                .ToListAsync(cancellationToken);
            var groups = records
                .GroupBy(item => new { item.WeekStart, item.WeekEnd })
                .Select(group => new FuelAdjustmentWeek(
                    group.Key.WeekStart, group.Key.WeekEnd, group.ToList()))
                .ToList();
            return ApiResults.Ok(
                new FuelAdjustmentFeed(groups.Count, weeks, groups),
                "fuel_price_adjustments", instanceId);
        });

        app.MapPost(ApiRoutes.AdminFuelPriceAdjustmentsImport, async (
            HttpRequest request,
            AppDbContext db,
            FuelAdjustmentImporter importer,
            FuelAdjustmentBackfill backfill,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (request.Query.ContainsKey("from") || request.Query.ContainsKey("to"))
                return await FuelAdjustmentBackfill.StartRequestAsync(request,
                    backfill, importer, timeProvider, instanceId, cancellationToken);
            string? sourceUrl = null;
            foreach (var parameter in request.Query)
            {
                if (parameter.Key != "sourceUrl" || parameter.Value.Count != 1)
                    return ApiResults.BadRequest("Only one sourceUrl query parameter is accepted.", instanceId);
                sourceUrl = parameter.Value[0];
            }

            if (sourceUrl is not null)
            {
                try { sourceUrl = FuelAdjustmentImporter.ValidatePdfUrl(sourceUrl); }
                catch (ArgumentException exception)
                { return ApiResults.BadRequest(exception.Message, instanceId); }
            }
            if (!importer.IsConfigured)
                return ApiResults.ServiceUnavailable(
                    "fuel_adjustment_extractor_not_configured", instanceId,
                    "OPENAI_API_KEY is required to import DOE notices.");

            try
            {
                var result = await ImportOneAsync(db, importer, timeProvider, sourceUrl,
                    null, null, cancellationToken);
                return ApiResults.Ok(result!,
                    $"fuel_adjustments_{result!.Status}",
                    instanceId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ApiResults.GatewayTimeout("fuel_adjustment_import_timeout", instanceId);
            }
            catch (HttpRequestException)
            {
                return ApiResults.BadGateway("fuel_adjustment_upstream_unavailable", instanceId);
            }
            catch (FuelAdjustmentExtractionException exception)
            {
                return ApiResults.BadGateway("fuel_adjustment_extraction_invalid", instanceId, exception.Message);
            }
            catch (InvalidDataException exception)
            {
                return ApiResults.BadGateway("fuel_adjustment_import_invalid", instanceId, exception.Message);
            }
            catch (JsonException)
            {
                return ApiResults.BadGateway("fuel_adjustment_import_invalid", instanceId,
                    "The extractor returned malformed JSON.");
            }
        });

        app.MapGet(ApiRoutes.AdminFuelPriceAdjustmentsImportJob, (
            string jobId, FuelAdjustmentBackfill backfill) =>
        {
            if (!Guid.TryParse(jobId, out var id))
                return ApiResults.BadRequest("jobId must be a GUID.", instanceId);
            var status = backfill.Get(id);
            return status is null
                ? ApiResults.NotFound("Fuel-adjustment import job was not found.",
                    instanceId, ApiRoutes.AdminFuelPriceAdjustmentsImportJob)
                : ApiResults.Ok(status, "fuel_adjustment_backfill_status", instanceId);
        });
    }

    internal static async Task<FuelAdjustmentImportResult?> ImportOneAsync(
        AppDbContext db, FuelAdjustmentImporter importer, TimeProvider timeProvider,
        string? sourceUrl, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var (url, extraction) = await importer.ImportAsync(sourceUrl, cancellationToken);
        if (!TryValidate(extraction, out var weekStart, out var weekEnd,
            out var rows, out var error))
            throw new FuelAdjustmentExtractionException(error);
        if (from.HasValue && to.HasValue &&
            (weekStart > to.Value || weekEnd < from.Value))
            return null;

        var existing = await db.FuelAdjustments
            .Where(a => a.WeekStart == weekStart)
            .ToListAsync(cancellationToken);
        var fromSource = existing.Where(a => a.SourceUrl == url).ToList();
        var alreadyImported = rows.All(row =>
            fromSource.Any(a =>
                a.OilCompany.Equals(row.OilCompany, StringComparison.OrdinalIgnoreCase) &&
                a.EffectiveDatePhilippines == row.EffectiveDatePhilippines &&
                a.EffectiveAtUtc == row.EffectiveAtUtc &&
                a.WeekEnd == weekEnd &&
                a.GasolineChangePerLiter == row.GasolineChangePerLiter &&
                a.DieselChangePerLiter == row.DieselChangePerLiter &&
                a.KeroseneChangePerLiter == row.KeroseneChangePerLiter));
        if (alreadyImported)
            return new FuelAdjustmentImportResult(
                weekStart, weekEnd, url, 0, 0, fromSource.Max(a => a.FetchedAtUtc), "already_imported");

        var fetchedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var added = 0;
        var updated = 0;
        foreach (var row in rows)
        {
            var match = existing.FirstOrDefault(a =>
                a.OilCompany.Equals(row.OilCompany, StringComparison.OrdinalIgnoreCase) &&
                a.EffectiveDatePhilippines == row.EffectiveDatePhilippines &&
                a.EffectiveAtUtc == row.EffectiveAtUtc);
            if (match is null)
            {
                match = new FuelAdjustment
                {
                    WeekStart = weekStart,
                    OilCompany = row.OilCompany,
                    EffectiveDatePhilippines = row.EffectiveDatePhilippines,
                    EffectiveAtUtc = row.EffectiveAtUtc
                };
                db.FuelAdjustments.Add(match);
                existing.Add(match);
                added++;
            }
            else if (match.WeekEnd != weekEnd ||
                match.GasolineChangePerLiter != row.GasolineChangePerLiter ||
                match.DieselChangePerLiter != row.DieselChangePerLiter ||
                match.KeroseneChangePerLiter != row.KeroseneChangePerLiter ||
                match.SourceUrl != url)
            {
                updated++;
            }
            else
            {
                continue;
            }

            match.WeekEnd = weekEnd;
            match.GasolineChangePerLiter = row.GasolineChangePerLiter;
            match.DieselChangePerLiter = row.DieselChangePerLiter;
            match.KeroseneChangePerLiter = row.KeroseneChangePerLiter;
            match.SourceUrl = url;
            match.FetchedAtUtc = fetchedAtUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new FuelAdjustmentImportResult(
            weekStart, weekEnd, url, added, updated, fetchedAtUtc,
            updated > 0 || fromSource.Count > 0 ? "updated" : "imported");
    }

    private sealed record ValidatedRow(
        string OilCompany,
        DateOnly EffectiveDatePhilippines,
        DateTime? EffectiveAtUtc,
        decimal? GasolineChangePerLiter,
        decimal? DieselChangePerLiter,
        decimal? KeroseneChangePerLiter);

    private static bool TryValidate(
        FuelAdjustmentExtraction extraction,
        out DateOnly weekStart,
        out DateOnly weekEnd,
        out List<ValidatedRow> rows,
        out string error)
    {
        rows = [];
        weekStart = default;
        weekEnd = default;
        error = "The DOE adjustment extraction has invalid dates or rows.";
        if (!DateOnly.TryParseExact(extraction.WeekStart, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out weekStart) ||
            !DateOnly.TryParseExact(extraction.WeekEnd, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out weekEnd) ||
            weekStart.DayOfWeek != DayOfWeek.Tuesday ||
            weekEnd != weekStart.AddDays(6))
            return false;

        if (extraction.Rows is null or { Count: < 1 or > 100 })
        {
            error = "The DOE adjustment extraction returned no rows or too many rows.";
            return false;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in extraction.Rows)
        {
            var company = row.OilCompany?.Trim();
            if (string.IsNullOrWhiteSpace(company) || company.Length > 100 ||
                !DateOnly.TryParseExact(row.EffectiveDatePhilippines, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveDate) ||
                effectiveDate < weekStart || effectiveDate > weekEnd ||
                (row.GasolineChangePerLiter is null && row.DieselChangePerLiter is null &&
                 row.KeroseneChangePerLiter is null) ||
                !ValidAmount(row.GasolineChangePerLiter) ||
                !ValidAmount(row.DieselChangePerLiter) ||
                !ValidAmount(row.KeroseneChangePerLiter))
                return false;

            DateTime? effectiveAtUtc = null;
            if (row.EffectiveTimePhilippines is not null)
            {
                if (!TimeOnly.TryParseExact(row.EffectiveTimePhilippines, "HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveTime))
                    return false;
                effectiveAtUtc = new DateTimeOffset(effectiveDate.ToDateTime(effectiveTime),
                    TimeSpan.FromHours(8)).UtcDateTime;
            }

            var key = company + "\u001f" + effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                + "\u001f" + (row.EffectiveTimePhilippines ?? "date-only");
            if (!keys.Add(key))
                return false;
            rows.Add(new ValidatedRow(company, effectiveDate, effectiveAtUtc,
                row.GasolineChangePerLiter, row.DieselChangePerLiter,
                row.KeroseneChangePerLiter));
        }

        error = "";
        return true;
    }

    private static bool ValidAmount(decimal? amount) =>
        amount is null || (amount >= -100 && amount <= 100 &&
            decimal.Round(amount.Value, 2) == amount.Value);
}
