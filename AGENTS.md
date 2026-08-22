# BackendServer Agent Guide

## Project overview

BackendServer is a small ASP.NET Core Minimal API for game-server status, guest authentication, and persistent player data. It targets .NET 10, uses Entity Framework Core with PostgreSQL/Npgsql, and is deployed behind a reverse proxy such as Render.

## Repository map

- `Program.cs`: application composition, middleware ordering, public/admin endpoint mapping, and in-memory server status.
- `Auth.cs`: guest login, logout, token generation, device identity, and player-name handling.
- `PlayerDataEndpoints.cs`: authenticated player-data reads and patches.
- `AppDbContext.cs`: EF Core model configuration and database connection resolution.
- `ServerData.cs`: API contracts, routes, route registries, and API version.
- `ApiResults.cs`: shared response envelope helpers and API metadata.
- `Middleware/`: rate limiting and admin authentication.
- `Migrations/`: committed EF Core migrations and model snapshot.
- `docker-compose.yml`: local API and PostgreSQL stack.
- `out/` and `output/`: generated artifacts; do not edit or treat them as source.

## Common commands

Run commands from the repository root.

```bash
dotnet restore
dotnet build BackendServer.sln
dotnet run --project BackendServer.csproj
docker compose up --build
```

The local Docker API listens on `http://localhost:8080`; PostgreSQL is exposed on host port `5433`.

There is currently no automated test project. For code changes, at minimum run:

```bash
dotnet build BackendServer.sln
```

When behavior changes, also exercise the affected endpoint against a local instance when practical. Do not claim tests passed unless they were actually run.

## Implementation conventions

- Keep the Minimal API style already used by the project.
- Enable and respect nullable reference types; do not silence warnings without a concrete reason.
- Prefer small endpoint-mapping extension classes over adding more logic directly to `Program.cs`.
- Keep database and network operations asynchronous.
- Use `AsNoTracking()` for read-only EF Core queries where entity tracking is unnecessary.
- Reuse `ApiResults` so responses retain the `ApiResponse<T>` envelope and application error-code conventions.
- Put route strings in `ApiRoutes`; do not scatter route literals throughout the code.
- When adding or removing an endpoint, update the appropriate `RouteRegistry` collection and the rate-limit configuration when applicable.
- Preserve middleware ordering unless the change explicitly requires otherwise. Forwarded headers must run before IP-dependent behavior, and rate limiting must run before admin authentication.
- Use UTC for persisted timestamps and API timestamps.
- Avoid unrelated formatting or refactoring while making a focused change.

## Authentication and security

- Public player-data endpoints require both `Authorization: Bearer <token>` and `X-Device-Id`.
- Admin endpoints under `/admin` require `ADMIN_API_KEY` through `AdminAuthMiddleware`.
- Never commit production secrets, tokens, database credentials, or copied environment files.
- The development values in `docker-compose.yml` are local-only defaults.
- Do not weaken authentication, input validation, rate limiting, or cookie security to make a test pass.
- Treat player-state mutation as security-sensitive. Validate field names, types, ranges, ownership, and authorization before saving.
- Do not log bearer tokens, admin keys, `AUTH_SECRET`, database passwords, or full connection strings.

## Database changes

- Schema changes require an EF Core migration and an updated model snapshot; do not edit only the entity model.
- Review generated migrations before committing them.
- Preserve existing table/column naming conventions (`snake_case`) and PostgreSQL `jsonb` mappings.
- Avoid destructive or irreversible migrations unless the task explicitly requires them and the impact is documented.
- The application applies pending migrations during startup, so startup compatibility matters.

Create a migration with a descriptive PascalCase name:

```bash
dotnet ef migrations add DescriptiveMigrationName
```

## API and compatibility rules

- Preserve the existing JSON contract unless the task explicitly requests a breaking change.
- Prefer adding canonical routes over silently repurposing existing routes.
- Keep legacy aliases only when compatibility is intentional; mark them with `IsLegacy` in the route registry.
- Return precise `400`, `401`, `404`, and `429` responses through the shared response format.
- Validate JSON content type, malformed JSON, unknown properties, and value ranges at the API boundary.
- The server lifecycle state in `Program.cs` is an in-memory status model, not control of the host process. Do not describe it as infrastructure orchestration.

## Versioning and changelog

- Do not bump the version for ordinary development work unless the task asks for a release/version change.
- When releasing, keep `<Version>` in `BackendServer.csproj` and `APIVersion.Version` in `ServerData.cs` synchronized.
- Record user-visible changes under `Unreleased` in `CHANGELOG.md`, following the existing Added/Changed/Fixed/Removed structure.

## Git workflow and handoff

- Whenever a task adds a feature or changes repository files, always include both a suggested branch name and a suggested commit message in the final handoff.
- Branch names must use a meaningful change-type prefix and a short lowercase kebab-case description: `<type>/<short-description>`.
- Use prefixes such as `feature/`, `fix/`, `add/`, `refactor/`, `docs/`, `test/`, or `chore/` as appropriate.
- Never prefix a branch name with `codex/`.
- Use Conventional Commit-style messages such as `feat: add player profile endpoint`, `fix: reject invalid player tokens`, or `docs: document local setup`.
- Keep commit-message summaries imperative, specific, and concise.
- Suggesting a branch name and commit message does not authorize creating a branch or making a commit. Only perform those Git operations when the user explicitly requests them.

## Completion checklist

Before handing off a change:

1. Review `git diff` and preserve unrelated user changes.
2. Confirm generated directories such as `out/` and `output/` were not edited as source.
3. Run `dotnet build BackendServer.sln`.
4. Run focused endpoint or migration checks when relevant.
5. Summarize changed behavior, verification performed, and any remaining risk.
6. Provide the suggested branch name and commit message for the completed change.
