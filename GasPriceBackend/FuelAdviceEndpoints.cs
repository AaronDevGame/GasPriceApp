public static class FuelAdviceEndpoints
{
    public static void MapFuelAdviceEndpoints(this WebApplication app, string instanceId)
    {
        // Like news reads, this exposes public price information and no player data.
        app.MapGet(ApiRoutes.FuelAdvice, async (HttpRequest request, HttpResponse response,
            FuelAdviceService service, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "no-store";
            if (request.Query.Any(q => q.Key is not ("fuelType" or "oilCompany") || q.Value.Count != 1))
                return ApiResults.BadRequest("Only single fuelType and oilCompany parameters are accepted.", instanceId);
            var fuelType = request.Query["fuelType"].ToString();
            if (fuelType is not ("gasoline" or "diesel" or "kerosene"))
                return ApiResults.BadRequest("fuelType must be gasoline, diesel, or kerosene.", instanceId);
            string? company = null;
            if (request.Query.TryGetValue("oilCompany", out var value))
            {
                company = value.ToString();
                if (company.Length is < 2 or > 100 || company != company.Trim() ||
                    company.Any(char.IsControl) || company.Contains('<') || company.Contains('>'))
                    return ApiResults.BadRequest("oilCompany must be 2–100 plain-text characters without surrounding whitespace.", instanceId);
            }
            return ApiResults.Ok(await service.GetAsync(fuelType, company, ct), "fuel_advice", instanceId);
        });
    }
}
