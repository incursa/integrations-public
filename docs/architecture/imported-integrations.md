# Imported integration ownership

This repository owns public provider packages that were consolidated from earlier source trees. Local clone paths, source-worktree state, commits, and non-public implementation details are not part of the public contract.

## Public provider families

- Postmark, PostgreSQL, and SQL Server email providers
- WorkOS access, audit, webhook, and ASP.NET Core adapters
- Cloudflare, custom-domain, DNS, and KV adapters
- Electronic Notary public protocol and abstractions
- Azure Storage and Stripe integrations
- In-memory, PostgreSQL, SQLite, and SQL Server platform providers

The source tree and package catalog are authoritative for the exact project list.

## Provider-neutral ownership

Provider-neutral contracts and orchestration remain in [incursa/platform](https://github.com/incursa/platform), including access, custom-domain, DNS, email, storage, observability, and hosting abstractions.

## Boundary rules

- Public provider behavior and tests live here.
- Provider-neutral behavior lives in `platform`.
- Proprietary implementations and customer workflow remain outside public repositories.
- CI uses declared dependencies; local development overrides must be explicit and must not assume a machine-specific sibling path.
- Source consolidation does not authorize package renames, version resets, publication, or compatibility claims.
