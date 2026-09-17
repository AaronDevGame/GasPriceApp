public static class AdminChangelogEndpoints
{
    public static IEndpointRouteBuilder MapAdminChangelogEndpoint(this IEndpointRouteBuilder endpoints)
    {
        var changelogPath = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");

        endpoints.MapGet(ApiRoutes.AdminChangelog, () =>
            Results.File(changelogPath, "text/markdown; charset=utf-8"));

        return endpoints;
    }
}
