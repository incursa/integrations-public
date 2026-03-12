# Imported Integration Provenance

This repository preserves the provenance of the public provider packages that were previously consolidated into `platform` and are now extracted into `integrations-public`.

## `C:\src\incursa\integrations-postmark`

Current public landing zone:

- `src/Incursa.Platform.Email.Postmark/`
- `src/Incursa.Platform.Email.SqlServer/`
- `src/Incursa.Platform.Email.Postgres/`
- `tests/Incursa.Platform.Email.Tests/`

Provider-neutral packages retained in `platform`:

- `src/Incursa.Platform.Email/`
- `src/Incursa.Platform.Email.AspNetCore/`

## `C:\src\incursa\integrations-workos`

Current public landing zone:

- `src/Incursa.Integrations.WorkOS/`
- `src/Incursa.Integrations.WorkOS.Abstractions/`
- `src/Incursa.Integrations.WorkOS.Access/`
- `src/Incursa.Integrations.WorkOS.AspNetCore/`
- `src/Incursa.Integrations.WorkOS.Audit/`
- `src/Incursa.Integrations.WorkOS.Webhooks/`
- `tests/Incursa.Integrations.WorkOS.Tests/`
- `tests/Incursa.Integrations.WorkOS.Webhooks.Tests/`
- `tests/Incursa.Integrations.WorkOS.Audit.Tests/`

Provider-neutral capability packages retained in `platform`:

- `src/Incursa.Platform.Access/`
- `src/Incursa.Platform.Access.AspNetCore/`
- `src/Incursa.Platform.Access.Razor/`

## `C:\src\incursa\integrations-cloudflare`

Current public landing zone:

- `src/Incursa.Integrations.Cloudflare/`
- `src/Incursa.Integrations.Cloudflare.CustomDomains/`
- `src/Incursa.Integrations.Cloudflare.Dns/`
- `src/Incursa.Integrations.Cloudflare.KvProbe/`
- `tests/Incursa.Integrations.Cloudflare.Tests/`
- `tests/Incursa.Integrations.Cloudflare.IntegrationTests/`
- `tests/Incursa.Platform.CustomDomains.Tests/`
- `tests/Incursa.Platform.Dns.Tests/`

Provider-neutral capability packages retained in `platform`:

- `src/Incursa.Platform.CustomDomains/`
- `src/Incursa.Platform.Dns/`

## `C:\src\incursa\integrations-electronicnotary`

Current public landing zone:

- `src/Incursa.Integrations.ElectronicNotary/`
- `src/Incursa.Integrations.ElectronicNotary.Abstractions/`
- `tests/Incursa.Integrations.ElectronicNotary.Tests/`

Provider-specific Proof implementation moved to `integrations-private`:

- `src/Incursa.Integrations.Proof/`
- `src/Incursa.Integrations.Proof.AspNetCore/`

## Summary

- public provider packages now live here
- provider-neutral capability packages remain in `platform`
- local development still uses sibling `ProjectReference` links while packaging boundaries settle
