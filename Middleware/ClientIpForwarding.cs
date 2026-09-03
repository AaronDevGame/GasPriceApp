using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

public static class ClientIpForwarding
{
    public static IApplicationBuilder UseClientIpForwarding(
        this IApplicationBuilder app, IConfiguration configuration)
    {
        var isRenderWebService =
            string.Equals(configuration["RENDER"], "true", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(configuration["RENDER_SERVICE_TYPE"], "web", StringComparison.OrdinalIgnoreCase);

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Render's public edge overwrites this single-address header.
            // X-Forwarded-For can contain caller-supplied entries and internal hops.
            ForwardedForHeaderName = isRenderWebService ? "CF-Connecting-IP" : "X-Forwarded-For",
            ForwardLimit = 1
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        var networks = configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>();
        var proxies = configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>();

        // Render's ingress uses private 10/8 addresses. This trust applies only
        // to a Render public web service, whose internet traffic crosses its edge.
        // Explicit configuration replaces this default rather than widening it.
        if (networks is null && proxies is null && isRenderWebService)
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));

        foreach (var network in networks ?? [])
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        foreach (var proxy in proxies ?? [])
            options.KnownProxies.Add(IPAddress.Parse(proxy));

        // Empty trust lists mean "trust everyone" to ASP.NET, so do not enable
        // forwarded headers at all when no upstream proxy is configured.
        if (options.KnownIPNetworks.Count > 0 || options.KnownProxies.Count > 0)
            app.UseForwardedHeaders(options);

        // Persist and rate-limit the same canonical representation.
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress?.IsIPv4MappedToIPv6 == true)
                context.Connection.RemoteIpAddress = context.Connection.RemoteIpAddress.MapToIPv4();

            await next(context);
        });

        return app;
    }
}
