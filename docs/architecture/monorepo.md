# Repository Architecture

## Intent

`integrations-public` is the public provider-implementation repository in the Incursa split. It exists to hold concrete adapters and provider-backed implementations that depend on the provider-neutral `platform` repository.

## Zones

### `src/`

Packable public implementations that are specific to a provider or backing store.

Families currently shipped here:

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

### Storage coordination boundary

The SQLite provider implements the applicable scheduling, inbox/outbox, lease,
idempotency, fanout, operation, audit, and health contracts for one host and one
local database file. Its WAL, full-synchronization, transactional-migration,
busy-timeout/retry, integrity-check, and backup/restore guarantees are local
durability features, not distributed coordination. Do not place the SQLite file on a
shared or network filesystem or use it for multi-host leases, scheduling, inbox/outbox
dispatch, discovery, control-plane coordination, or central metrics rollups. It emits
local `System.Diagnostics.Metrics` counters for application-level collection. Use the
PostgreSQL or SQL Server providers for those scenarios. See the [SQLite provider
guide](../sqlite-platform-provider.md) for supported semantics, options, and recovery.

### `tests/`

Tests and smoke hosts that primarily validate the moved provider implementations. Some test projects still carry `Incursa.Platform.*` names because their package and namespace identities remain stable.

### `tools/`

Only tooling that is directly tied to the moved provider implementations should remain here. In the first split pass that means the schema migration CLI.

### `eng/`

Package catalog, affected-project, and versioning automation copied from `platform` and narrowed to this repo's remaining project tree.

### `docs/`

Architecture notes, scope rules, and repository split records.

## Solutions

- `Incursa.Integrations.Public.slnx` is the day-to-day solution for the retained integrations surface.
- `Incursa.Integrations.Public.CI.slnx` is the build/test/pack solution for this repo.

## Dependency boundary

- `integrations-public` may depend on `platform`
- `platform` must not depend on `integrations-public`
- cross-repository `ProjectReference` links are the temporary local-development bridge until package-based consumption is fully established

## Packaging policy

- keep package IDs stable even when they still use the `Incursa.Platform.*` naming family
- packability remains opt-in per project
- `eng/package-catalog.json` is the authoritative allowlist for this repo
- `eng/package-versions.json` is the authoritative per-package version manifest for packable projects
