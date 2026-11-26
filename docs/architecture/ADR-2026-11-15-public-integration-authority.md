# ADR: Public integration package authority

- Status: Accepted
- Date: 2026-11-15
- Decision owners: Incursa maintainers

## Context

The Cloudflare, WorkOS, Electronic Notary, and email provider package families
have existed in public, non-public, platform, and provider-specific source trees.
Some package IDs have been published from more than one repository and on unrelated
version lines. Proof also exists under two incompatible package and assembly
identity families. This makes source, release, migration, and rollback authority
ambiguous.

## Decision

`integrations-public` is the source and future release authority for publicly
distributed Cloudflare, WorkOS, generic Electronic Notary, and concrete email
provider packages. The authoritative email provider IDs are
`Incursa.Platform.Email.Postmark`, `Incursa.Platform.Email.Postgres`, and
`Incursa.Platform.Email.SqlServer`.
Package-family boundaries remain inside this repository; provider names do not
create standalone repository boundaries.

The provider-neutral email contracts, validation, outbox orchestration, in-memory
store, hosted processing, and idempotency cleanup remain platform responsibilities
under `Incursa.Platform.Email` and `Incursa.Platform.Email.AspNetCore`. Their source
is not copied into this repository. Public provider packages continue to consume
those stable platform package identities.

Non-public integrations remain outside this repository. Their repository names,
package identities, source layout, and release state are not part of the public
contract. A legacy public identity is not silently aliased, republished, or treated
as binary-compatible with a non-public implementation; any compatibility bridge
requires a separate consumer and support-lifetime decision.

The standalone `Incursa.Email.Abstractions`, `Incursa.Email.Core`,
`Incursa.Email.AspNetCore`, `Incursa.Email.Postmark`, `Incursa.Email.Postgres`, and
`Incursa.Email.SqlServer` identities are frozen retirement candidates. No active
consumer or published NuGet.org package was found for those identities. They must
not be introduced, published, or bridged without a separate consumer-backed
compatibility decision.

Existing package IDs and public assembly identities are preserved. Stable package
versions are not changed as part of source reconciliation. Producer cutover requires
an explicit release review after package and consumer validation.

## Consequences

- Later committed Cloudflare and WorkOS fixes must be reconciled here with tests and
  recorded provenance.
- Generic Electronic Notary remains public; non-public provider behavior remains outside this repository.
- Public-only Cloudflare CustomDomains/Dns and WorkOS Access/Audit/Webhooks projects
  remain authoritative and must not be replaced by the narrower standalone trees.
- A single monotonically increasing version line must be selected before the next
  stable publication.
- The three public email provider packages must continue above the existing
  `2026.3.9.853` release line; a lower semantic version such as `6.0.10` would be a
  version and assembly regression even if its code were newer.
- Standalone publish workflows are not disabled or repositories archived by this
  decision alone.
- Reconciliation does not authorize publication, producer cutover, workflow
  changes, or repository archival.

## Evidence

See [Cloudflare and WorkOS reconciliation ledger](cloudflare-workos-reconciliation.md).
See also [Electronic Notary public package authority](electronic-notary-proof-reconciliation.md).
See also [Postmark and email package authority](postmark-email-reconciliation.md).
