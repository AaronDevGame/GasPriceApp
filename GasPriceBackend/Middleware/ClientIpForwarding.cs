using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;

public static class ClientIpForwarding
{
    public const string ProxyClientIpHeader = "X-GasPrice-Client-IP";
    public const string ProxySecretHeader = "X-GasPrice-Proxy-Secret";

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

        // Render may deliver edge requests through private 10/8 or a local proxy.
        // This trust applies only to a Render public web service.
        // Explicit configuration replaces this default rather than widening it.
        if (networks is null && proxies is null && isRenderWebService)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        }

        foreach (var network in networks ?? [])
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        foreach (var proxy in proxies ?? [])
            options.KnownProxies.Add(IPAddress.Parse(proxy));

        // Empty trust lists mean "trust everyone" to ASP.NET, so do not enable
        // forwarded headers at all when no upstream proxy is configured.
        if (options.KnownIPNetworks.Count > 0 || options.KnownProxies.Count > 0)
            app.UseForwardedHeaders(options);

        var cloudflareProxySecret = configuration["CloudflareProxy:SharedSecret"];
        if (!string.IsNullOrEmpty(cloudflareProxySecret) && cloudflareProxySecret.Length < 32)
            throw new InvalidOperationException(
                "CloudflareProxy:SharedSecret must contain at least 32 characters when configured.");

        // Persist and rate-limit the same canonical representation.
        app.Use(async (context, next) =>
        {
            IPAddress? proxyClientAddress = null;
            var hasTrustedProxyAddress =
                !string.IsNullOrEmpty(cloudflareProxySecret) &&
                TryGetSingleHeader(context.Request, ProxySecretHeader, out var suppliedSecret) &&
                SecretsMatch(cloudflareProxySecret, suppliedSecret) &&
                TryGetSingleHeader(context.Request, ProxyClientIpHeader, out var suppliedAddress) &&
                IPAddress.TryParse(suppliedAddress, out proxyClientAddress);

            // The secret and internal forwarding value are gateway metadata, not
            // application request headers. Do not leave them available downstream.
            context.Request.Headers.Remove(ProxySecretHeader);
            context.Request.Headers.Remove(ProxyClientIpHeader);

            if (hasTrustedProxyAddress && proxyClientAddress is not null)
            {
                context.Connection.RemoteIpAddress = proxyClientAddress;
            }

            if (context.Connection.RemoteIpAddress?.IsIPv4MappedToIPv6 == true)
                context.Connection.RemoteIpAddress = context.Connection.RemoteIpAddress.MapToIPv4();

            await next(context);
        });

        return app;
    }

    private static bool TryGetSingleHeader(
        HttpRequest request,
        string headerName,
        out string value)
    {
        value = string.Empty;
        if (!request.Headers.TryGetValue(headerName, out var values) || values.Count != 1)
            return false;

        value = values[0] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool SecretsMatch(string expected, string supplied)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}
