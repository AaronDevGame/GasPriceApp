# GasPriceApp

GasPriceApp is a monorepo for the gas-price application.

- `GasPriceBackend/` contains the ASP.NET Core backend.
- `GasPriceClient/` is reserved for the Expo client targeting web, iOS, and Android.
- `GasPriceUnity/` reserves the paused Unity frontend project.

Build the backend from the repository root:

```bash
dotnet build GasPriceApp.sln
```

Run the local API and PostgreSQL stack:

```bash
docker compose up --build
```
