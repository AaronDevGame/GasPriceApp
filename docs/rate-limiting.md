# Guest login limits

`POST /auth/guest/login` shares limits across all devices using the same client
IP address. The defaults allow one request per second, at most 5 admitted
attempts in any rolling minute, and at most 30 in any rolling hour. Both new
account creation and existing-account login count, including requests that
subsequently fail validation or authentication. Rejected requests do not extend
the waiting period.

Changing `X-Device-Id`, the `device_id` cookie, route casing, or the trailing
slash does not give the client a new allowance. A rejected request receives
HTTP `429`, the existing API error envelope, and `Retry-After` in seconds.
Clients should wait for that duration before retrying.

Configure positive integer values in `appsettings.json` under
`RateLimiting:GuestLogin`, or use environment variables:

- `RateLimiting__GuestLogin__PermitLimitPerMinute` (default `5`)
- `RateLimiting__GuestLogin__PermitLimitPerHour` (default `30`)

Restart the app after changing these values. Players behind the same public IP
(for example, a school or mobile carrier) share the allowance. Adjust the
limits using actual traffic and failed-login patterns.

## Deployment requirements and limits

The limiter and guest persistence both use `RemoteIpAddress` after client-IP
forwarding. On Render public web services (`RENDER=true` and
`RENDER_SERVICE_TYPE=web`, supplied by Render), the app uses `CF-Connecting-IP`.
Render documents that its Cloudflare edge overwrites this header with the
client address. It avoids interpreting the chain of caller-supplied and
internal addresses in `X-Forwarded-For`. See
[Render's client-IP guidance](https://render.com/articles/host-pocketbase-on-render).

Forwarding on Render is accepted only from the private `10.0.0.0/8` ingress
network by default, not from arbitrary internet peers. This range is a
deployment assumption based on the observed Render proxy addresses, not a
published guarantee of immutable ingress ranges. The backend must remain
behind Render's public edge; other services able to connect over its private
network must be trusted. A header alone cannot authenticate a private-network
caller. Do not expose a separate route to the container that bypasses the edge.

`ReverseProxy:KnownNetworks` (CIDR strings) and `ReverseProxy:KnownProxies`
(individual IP strings) replace the default trust list when configured. For
example, `ReverseProxy__KnownProxies__0` supplies one proxy address. Use verified
ingress addresses; do not use unrestricted ranges. Outside Render, forwarding
is disabled unless a trust list is supplied; a configured non-Render proxy uses
one hop of `X-Forwarded-For`. Render private services require their own trusted
forwarding arrangement. See
[Microsoft's forwarded-header guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

After deployment, make guest-login requests from the same client connection
and inspect `guests.ip_address`. New records should show that connection's
public address instead of a `10.x.x.x` proxy. Repeat with an arbitrary
`X-Forwarded-For` header: the stored address should not change. A client-supplied
`CF-Connecting-IP` should also be overwritten by Render's edge. If private
addresses are still saved, check the Render service type, header delivery and
actual ingress addresses before changing the trust list. Never log credentials
or full request headers when investigating this.

The address may be IPv4 or IPv6; IPv4-mapped IPv6 is normalized to IPv4. One
Mac can use different public addresses if it switches address family, VPN,
proxy, or network. Existing rows cannot be backfilled with an unknown original
IP; their address updates on the next successful login.

Counters are held in memory per application instance and reset on restart.
Multiple instances do not share an allowance. Distributed abuse using many
IPs, including rotating IPv6 addresses, can still create accounts. For that
traffic, enforce shared limits at the gateway or in a shared store and consider
a verified challenge or platform attestation before creating a new account.
Device identifiers alone cannot establish that a request came from a unique
physical device.
