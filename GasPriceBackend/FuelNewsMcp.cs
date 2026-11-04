using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;

// This is a resource server, not a token issuer. Credentials remain with the OAuth provider.
public sealed record FuelNewsMcpSettings(bool Enabled, string? Authority, string? Resource, string[] AllowedSubjects)
{
    public static FuelNewsMcpSettings Read(IConfiguration config)
    {
        var enabled = config.GetValue<bool>("Mcp:Enabled");
        var authority = config["Mcp:Authority"];
        var resource = config["Mcp:Resource"];
        var subjects = (config["Mcp:AllowedSubjects"] ?? "").Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (enabled && (!HttpsUrl(authority) || !HttpsUrl(resource) ||
            new Uri(resource!).AbsolutePath != ApiRoutes.Mcp || subjects.Length == 0))
            throw new InvalidOperationException("Enabled MCP requires HTTPS Mcp:Authority, Mcp:Resource ending in /mcp, and Mcp:AllowedSubjects.");
        return new(enabled, authority, resource, subjects);
    }

    private static bool HttpsUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
}

public static class FuelNewsMcp
{
    public const string Scheme = "FuelNewsMcp";
    public const string ReadPolicy = "FuelNewsMcpRead";
    public const string PublishPolicy = "FuelNewsMcpPublish";
    public const string ReadScope = "fuel-news:read";
    public const string PublishScope = "fuel-news:publish";

    public static void AddFuelNewsMcp(this IServiceCollection services, IConfiguration config)
    {
        var settings = FuelNewsMcpSettings.Read(config);
        services.AddSingleton(settings);
        services.AddHttpContextAccessor();
        services.AddAuthentication().AddJwtBearer(Scheme, options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            options.IncludeErrorDetails = false;
            if (settings.Enabled)
            {
                options.Authority = settings.Authority;
                options.Audience = settings.Resource;
            }
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true,
                ValidAudience = settings.Resource, ValidIssuer = settings.Authority,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256],
                ClockSkew = TimeSpan.FromSeconds(30)
            };
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // Do not interpret existing guest/admin credentials as OAuth tokens on REST routes.
                    if (!settings.Enabled || !context.Request.Path.StartsWithSegments(ApiRoutes.Mcp)) context.NoResult();
                    return Task.CompletedTask;
                },
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    // Never derive OAuth URLs from an untrusted Host header or include token errors.
                    if (settings.Enabled)
                        context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{MetadataUrl(settings)}\", scope=\"{ReadScope}\"";
                    await ApiResults.Unauthorized("mcp_authentication_required",
                        context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["INSTANCE_ID"] ?? "unknown")
                        .ExecuteAsync(context.HttpContext);
                },
                OnForbidden = async context =>
                {
                    context.Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_scope\", scope=\"{ReadScope}\"";
                    await ApiResults.Forbidden("mcp_access_denied",
                        context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["INSTANCE_ID"] ?? "unknown")
                        .ExecuteAsync(context.HttpContext);
                }
            };
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReadPolicy, policy => policy.AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser().RequireAssertion(context => settings.Enabled &&
                    settings.AllowedSubjects.Contains(context.User.FindFirstValue("sub"), StringComparer.Ordinal) &&
                    HasScope(context.User, ReadScope)));
            options.AddPolicy(PublishPolicy, policy => policy.AddAuthenticationSchemes(Scheme)
                .RequireAuthenticatedUser().RequireAssertion(context => settings.Enabled &&
                    settings.AllowedSubjects.Contains(context.User.FindFirstValue("sub"), StringComparer.Ordinal) &&
                    HasScope(context.User, ReadScope) && HasScope(context.User, PublishScope)));
        });
        services.AddMcpServer().WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateless;
        }).AddAuthorizationFilters().WithTools<FuelNewsMcpTools>(new JsonSerializerOptions(FuelNewsValidation.Json)
        {
            // Schema export needs more depth than wire JSON; the boundary still limits requests to 8.
            MaxDepth = 64
        });
    }

    private static bool HasScope(ClaimsPrincipal user, string scope) => user.FindAll("scope")
        .Concat(user.FindAll("scp")).Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal));

    private static string MetadataUrl(FuelNewsMcpSettings settings) =>
        new Uri(new Uri(settings.Resource!), ApiRoutes.McpMetadata).AbsoluteUri;

    public static void UseFuelNewsMcpBoundary(this WebApplication app, string instanceId)
    {
        var settings = app.Services.GetRequiredService<FuelNewsMcpSettings>();
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(ApiRoutes.Mcp)) { await next(); return; }
            if (!settings.Enabled)
            {
                await ApiResults.ServiceUnavailable("mcp_not_configured", instanceId).ExecuteAsync(context);
                return;
            }
            // Server-to-server callers omit Origin. Browser callers must use the configured origin.
            if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                origin.ToString() != new Uri(settings.Resource!).GetLeftPart(UriPartial.Authority))
            {
                await ApiResults.Forbidden("mcp_origin_not_allowed", instanceId).ExecuteAsync(context);
                return;
            }
            if (HttpMethods.IsPost(context.Request.Method) && context.User.Identity?.IsAuthenticated == true &&
                (await context.RequestServices.GetRequiredService<IAuthorizationService>()
                    .AuthorizeAsync(context.User, null, ReadPolicy)).Succeeded)
            {
                try
                {
                    // Also bound chunked JSON-RPC bodies and reject duplicates before SDK binding.
                    var body = await FuelNewsEndpoints.ReadJsonAsync<JsonElement>(context.Request, context.RequestAborted);
                    if (body.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Provide a JSON-RPC object; batches are not supported.");
                    ValidateArguments(body);
                    var original = context.Request.Body;
                    await using var buffered = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(body));
                    context.Request.Body = buffered;
                    try { await next(); }
                    finally { context.Request.Body = original; }
                    return;
                }
                catch (InvalidDataException ex)
                {
                    await ApiResults.BadRequest(ex.Message, instanceId).ExecuteAsync(context);
                    return;
                }
            }
            await next();
        });
    }

    private static void ValidateArguments(JsonElement body)
    {
        if (!body.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
            method.GetString() != "tools/call" || !body.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || !parameters.TryGetProperty("arguments", out var arguments)) return;
        string[]? allowed = name.GetString() switch
        {
            "list_news" => ["limit", "cursor"], "read_article" => ["id"],
            "publish_or_update" => ["article", "expectedRevision"], _ => null
        };
        if (allowed is null) return;
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(p => !allowed.Contains(p.Name)))
            throw new InvalidDataException("Unknown tool argument or invalid argument object.");
        if (name.GetString() == "publish_or_update")
        {
            try { _ = arguments.Deserialize<FuelNewsImport>(FuelNewsValidation.Json); }
            catch (JsonException) { throw new InvalidDataException("Provide article and expectedRevision with all required fields and no unknown properties."); }
        }
    }

    public static void MapFuelNewsMcp(this WebApplication app, string instanceId)
    {
        var settings = app.Services.GetRequiredService<FuelNewsMcpSettings>();
        app.MapGet(ApiRoutes.McpMetadata, () => settings.Enabled
            ? Results.Json(new
            {
                resource = settings.Resource, authorization_servers = new[] { settings.Authority },
                scopes_supported = new[] { ReadScope, PublishScope }, bearer_methods_supported = new[] { "header" }
            })
            : ApiResults.ServiceUnavailable("mcp_not_configured", instanceId));
        app.MapMcp(ApiRoutes.Mcp).RequireAuthorization(ReadPolicy);
    }
}
