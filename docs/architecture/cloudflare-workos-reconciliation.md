# Cloudflare and WorkOS public package authority

## Decision

`integrations-public` is authoritative for the public Cloudflare and WorkOS package families. Provider-neutral access, storage, DNS, webhooks, and hosting contracts remain owned by `platform`.

## Package boundary

- Cloudflare packages own Cloudflare protocol calls, DNS, custom-domain, and KV behavior.
- WorkOS packages own WorkOS protocol calls, access adapters, audit, webhooks, and ASP.NET Core registration.
- Public packages must not embed deployment credentials, private operational configuration, customer data, or host-specific workflow.
- Package identities remain stable unless an explicit compatibility and migration decision says otherwise.

## Compatibility

Source movement does not by itself authorize a package rename, version reset, alias, or retirement. Release automation must compare the package manifest and published API baseline and choose a version that preserves feed continuity.

## Verification

Changes require focused provider tests, package inspection, public API compatibility checks when applicable, and validation of the one-way dependency on `platform`. Point-in-time commits, worktrees, test counts, feed observations, and deployment state belong in pull requests or release records rather than this decision.
