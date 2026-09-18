# GasPriceFrontend

Unity 6 frontend API samples live under `Assets/_ProjectSpecific`.

- `APIConstants` owns the base URL, endpoint paths, header names, and request timeout.
- `APIManager` sends requests and keeps bearer/admin credentials in memory only.
- `APIEventsServer` exposes one call per canonical backend operation and publishes shared success/failure events.
- `APIDtos` groups request and response DTOs by API domain. Prefer one request/response DTO per contract; a separate file per endpoint is optional and only useful when a domain file becomes difficult to navigate.
- `APICallingSampleScript` is attached to each generated endpoint row. It has serialized sample input fields, a serialized result label, a Canvas button listener, and a NaughtyAttributes Inspector button.
- `APIEndpointCanvasBuilder` is attached to `GameScene`'s Canvas and creates a scrollable row for every endpoint at runtime.

Call **GuestLogin** before player-authorized samples. The returned access token is applied as `Authorization: Bearer <token>` only at runtime. Admin rows provide a password-style runtime input for the admin bearer token. Neither token is serialized or saved to `PlayerPrefs`.
