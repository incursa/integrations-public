# Repository boundary summary

## Integration repository ownership

`integrations-public` owns public provider-specific packages, their focused tests and smoke hosts, and schema migration tooling that is inseparable from those providers.

The source tree and [eng/package-catalog.json](../eng/package-catalog.json) are authoritative for the exact packable project inventory. Documentation should describe package families and boundaries rather than copying a point-in-time project count.

## Platform ownership

[incursa/platform](https://github.com/incursa/platform) owns provider-neutral abstractions, models, orchestration, hosting adapters, shared test utilities, analyzers, and helper CLIs.

Dependency direction is one way: public provider implementations may depend on `platform`; `platform` must not depend on this repository.

## Development dependencies

CI and releases consume declared package dependencies. Contributors may use explicit local project or package overrides for coordinated changes, but durable docs and project files must not assume a machine-specific sibling checkout.

## SQLite scope

`Incursa.Platform.Sqlite` is a single-host, local-file provider. It provides WAL with full synchronization, transactional migrations, bounded busy and locked retries, integrity checks, and backup and restore. It is not a distributed coordination provider; use PostgreSQL or SQL Server for multi-host coordination.

## Non-public boundary

Proprietary integrations, customer-specific workflows, private endpoints, credentials, and non-public source topology are outside this repository and must not be named or described in public documentation.
