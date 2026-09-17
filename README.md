# GasPriceApp

GasPriceApp is a monorepo for the gas-price application.

- `GasPriceBackend/` contains the ASP.NET Core backend.
- `GasPriceFrontend/` is reserved for the Unity frontend.

Build the backend from the repository root:

```bash
dotnet build GasPriceApp.sln
```

Run the local API and PostgreSQL stack:

```bash
docker compose up --build
```
