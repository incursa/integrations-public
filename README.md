# Incursa Integrations Public

`integrations-public` contains public provider-specific implementations that build on the provider-neutral packages in the sibling `platform` repository.

This repository owns:

- vendor-specific adapters under `src/Incursa.Integrations.*`
- public provider implementations that still use `Incursa.Platform.*` package names
- provider-focused tests and smoke hosts
- schema migration tooling required by the moved storage providers

Dependency direction:

- `integrations-public` can depend on `platform`
- `platform` must not depend on `integrations-public`
- future proprietary connectors should live in `integrations-private`

Local development currently uses cross-repository `ProjectReference` links back to [platform](C:/src/incursa/platform) for shared abstractions and provider-neutral test/tooling assets.

## Start Here

- [Repository architecture](docs/architecture/monorepo.md)
- [Imported integration history](docs/architecture/imported-integrations.md)
- [Repository scope boundary](docs/quality/repo-scope-boundary.md)
- [SQLite Platform provider](docs/sqlite-platform-provider.md)
- [Split plan](docs/repo-split-plan.md)
- [Split summary](docs/repository-split-summary.md)
- [Curated repo map](llms.txt)

## Repository Layout

- [`src/`](src/) contains public provider-specific packages and provider-backed implementations.
- [`tests/`](tests/) contains provider-focused tests, smoke hosts, and minimal shared test utilities required by this repo.
- [`docs/`](docs/) contains architecture notes, scope rules, and migration records.
- [`eng/`](eng/) contains package catalog, versioning, and release helpers.
- [`tools/`](tools/) currently contains the schema migration CLI that remains tied to the moved provider packages.

## Current Package Families

- `Incursa.Integrations.Cloudflare*`
- `Incursa.Integrations.ElectronicNotary*`
- `Incursa.Integrations.Storage.Azure`
- `Incursa.Integrations.Stripe`
- `Incursa.Integrations.WorkOS*`
- `Incursa.Platform.Email.Postmark`
- `Incursa.Platform.Email.Postgres`
- `Incursa.Platform.Email.SqlServer`
- `Incursa.Platform.InMemory`
- `Incursa.Platform.Postgres`
- `Incursa.Platform.Sqlite`
- `Incursa.Platform.SqlServer`

### SQLite coordination boundary

`Incursa.Platform.Sqlite` provides the applicable Platform scheduling, inbox/outbox,
lease, idempotency, fanout, operation, audit, and health contracts for one host and
one local database file. It enables WAL mode with full synchronization, transactional
migrations, bounded busy-timeout/retry handling, integrity checks, and online
backup/restore. Keep the file off shared or network filesystems. SQLite does not
provide multi-host leases, schedulers, inbox/outbox dispatch, discovery, control-plane
coordination, or central metrics rollups. It emits local `System.Diagnostics.Metrics`
counters that applications may export with OpenTelemetry. Use `Incursa.Platform.Postgres` or
`Incursa.Platform.SqlServer` for those scenarios. See the [SQLite provider guide](docs/sqlite-platform-provider.md)
for registration, support boundaries, recovery, and cutover procedures.

## Local Validation

```powershell
dotnet restore Incursa.Integrations.Public.CI.slnx
dotnet tool restore
dotnet build Incursa.Integrations.Public.CI.slnx -c Release
dotnet test Incursa.Integrations.Public.CI.slnx -c Release
pwsh -File eng/Generate-PackageCatalog.ps1
pwsh -File eng/Pack-PublicPackages.ps1 -Configuration Release -OutputPath ./nupkgs
```
