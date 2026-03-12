# Repository Split Summary

## What moved here from `platform`

Source projects:

- `src/Incursa.Integrations.Cloudflare/`
- `src/Incursa.Integrations.Cloudflare.CustomDomains/`
- `src/Incursa.Integrations.Cloudflare.Dns/`
- `src/Incursa.Integrations.Cloudflare.KvProbe/`
- `src/Incursa.Integrations.ElectronicNotary/`
- `src/Incursa.Integrations.ElectronicNotary.Abstractions/`
- `src/Incursa.Integrations.Storage.Azure/`
- `src/Incursa.Integrations.Stripe/`
- `src/Incursa.Integrations.WorkOS/`
- `src/Incursa.Integrations.WorkOS.Abstractions/`
- `src/Incursa.Integrations.WorkOS.Access/`
- `src/Incursa.Integrations.WorkOS.AspNetCore/`
- `src/Incursa.Integrations.WorkOS.Audit/`
- `src/Incursa.Integrations.WorkOS.Webhooks/`
- `src/Incursa.Platform.Email.Postmark/`
- `src/Incursa.Platform.Email.Postgres/`
- `src/Incursa.Platform.Email.SqlServer/`
- `src/Incursa.Platform.InMemory/`
- `src/Incursa.Platform.Postgres/`
- `src/Incursa.Platform.SqlServer/`

Tests and tooling:

- `tests/Incursa.Integrations.Cloudflare.IntegrationTests/`
- `tests/Incursa.Integrations.Cloudflare.Tests/`
- `tests/Incursa.Integrations.ElectronicNotary.Tests/`
- `tests/Incursa.Integrations.Storage.Azure.Tests/`
- `tests/Incursa.Integrations.WorkOS.Audit.Tests/`
- `tests/Incursa.Integrations.WorkOS.Tests/`
- `tests/Incursa.Integrations.WorkOS.Webhooks.Tests/`
- `tests/Incursa.Platform.Access.Tests/`
- `tests/Incursa.Platform.CustomDomains.Tests/`
- `tests/Incursa.Platform.Dns.Tests/`
- `tests/Incursa.Platform.Email.Tests/`
- `tests/Incursa.Platform.InMemory.Tests/`
- `tests/Incursa.Platform.Postgres.Tests/`
- `tests/Incursa.Platform.Smoke.AppHost/`
- `tests/Incursa.Platform.Smoke.ServiceDefaults/`
- `tests/Incursa.Platform.SmokeWeb/`
- `tests/Incursa.Platform.SqlServer.Tests/`
- `tools/migrations/src/Incursa.Platform.SchemaMigrations.Cli/`

## What remained in `platform`

- provider-neutral abstractions, models, orchestration, and hosting adapters
- shared provider-neutral tests
- shared analyzers and helper CLIs

## Current dependency bridge

- moved projects use sibling `ProjectReference` links to `../platform` for provider-neutral packages
- several moved test projects also reference the shared `Incursa.TestDocs.Analyzers` project from `platform`

## Private follow-up

- the provider-specific Proof implementation was later migrated out of this repo into `integrations-private` as `src/Incursa.Integrations.Proof/` and `src/Incursa.Integrations.Proof.AspNetCore/`

## Unresolved follow-up work

- replace sibling project references with package-based local development once publishing and feed workflows are established
- decide whether some moved test projects should later be renamed to better match their repository ownership without breaking current package or namespace identity
- revisit `integrations-private` once proprietary connectors are isolated enough to move safely
