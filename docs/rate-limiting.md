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

The middleware uses `RemoteIpAddress` after forwarded-header processing. The
current `Program.cs` clears `KnownProxies` and `KnownIPNetworks`, trusting all
upstream senders. This is safe only if the backend is reachable exclusively
through a proxy that supplies the actual client address in the rightmost
`X-Forwarded-For` entry. If clients can reach the backend directly or control
that entry, they can spoof an IP and evade these limits. Restrict backend
access and configure trusted proxy addresses/networks for the deployment; do
not copy arbitrary client headers directly into the rate-limit key. See
[Microsoft's forwarded-header guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

Counters are held in memory per application instance and reset on restart.
Multiple instances do not share an allowance. Distributed abuse using many
IPs, including rotating IPv6 addresses, can still create accounts. For that
traffic, enforce shared limits at the gateway or in a shared store and consider
a verified challenge or platform attestation before creating a new account.
Device identifiers alone cannot establish that a request came from a unique
physical device.
