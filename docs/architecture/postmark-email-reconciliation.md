# Postmark and email reconciliation ledger

- Reconciliation date: 2026-11-16
- Destination authority: `integrations-public`
- Read-only sources: `integrations-postmark` and `platform`
- Scope: source, API, behavior, tests, dependencies, consumers, package identity,
  version continuity, and validation
- Out of scope: publication, workflow changes, producer cutover, and archival

## Decision

`integrations-public` is authoritative for the concrete provider packages
`Incursa.Platform.Email.Postmark`, `Incursa.Platform.Email.Postgres`, and
`Incursa.Platform.Email.SqlServer`.

Provider-neutral contracts and orchestration remain platform-owned through
`Incursa.Platform.Email` and `Incursa.Platform.Email.AspNetCore`. The six standalone
`Incursa.Email.*` identities are frozen retirement candidates: repository and GitHub
consumer searches found no active package reference, and NuGet.org exposes no
published version of any of them. No compatibility packages or aliases are created
by this reconciliation.

## Source baselines

| Repository | Baseline | Working-tree constraint |
| --- | --- | --- |
| `integrations-public` | `bb636c9b3cecb0d15eb3209d6804180d058dcaea` | Preserve the existing Cloudflare, WorkOS, Electronic Notary, and documentation changes. |
| `integrations-private` | `d5f97cd018a2ff80ffa5872f3d81fa849ba80c7c` | Preserve the existing four-file Proof reconciliation slice. |
| `integrations-postmark` | `feb36d72a8f0272996cb3ebf745a6fcc71b27eae` | Read-only; eight pre-existing cleanup/tooling changes. |
| `platform` | `21d9588916c9a276e23aa5949d37626f495ac136` | Read-only; pre-existing metrics, workflow, and documentation changes. |

The same-ID platform package provenance is commit
`0e040ab320b828594ef5061c8497245d24dd2b42`, recorded in packages published as
`2026.3.9.853`. That commit was inspected from a remote source archive because it is
not present in the local platform object database. The last locally available
provider-source commit before removal is
`dc2e5ad4b6ceb80b5101f55664f638bcbedfe6ff`.

## Exhaustive standalone inventory and disposition

The standalone repository contains 101 committed project, source, and test paths:

| Area | Paths | Disposition |
| --- | ---: | --- |
| `Incursa.Email.Abstractions` | 29 | Superseded by the platform-owned `Incursa.Platform.Email` contract surface. |
| `Incursa.Email.Core` | 10 | Superseded by platform validation, metrics, outbox orchestration, in-memory storage, and policy behavior. |
| `Incursa.Email.AspNetCore` | 7 | Superseded by platform hosted processing and idempotency cleanup. |
| `Incursa.Email.Postmark` | 23 | Provider behavior already present in the public same-ID package. |
| `Incursa.Email.Postgres` | 11 | Provider behavior already present in the public same-ID package. |
| `Incursa.Email.SqlServer` | 5 | Provider behavior already present in the public same-ID package. |
| `Incursa.Email.Tests` | 16 | The public provider test project contains the equivalent suite; standalone compiles only `SanityTests.cs`. |

The standalone history was inspected commit by commit. `80510a75` introduced the
split platform provider packages; `8b62f5d0` renamed and split the code into the six
`Incursa.Email.*` identities; `dd8d6d41` changed dependency versions. The remaining
commits (`9ff53250`, `de192698`, `546085f`, `487b623`, `396aec4`, `1abe7dc`,
`1f72303`, and `feb36d72`) affect templates, workbench metadata, CI, or repository
maintenance rather than shipped email behavior.

## API and behavior comparison

The standalone neutral surface contains 28 contract/model types, including the
address, attachment, message, validation, delivery, probe, policy, outbox, store,
dispatcher, processor, and sender contracts. Its core adds nine implementation
types: audit events, validator, metrics, outbox facade, dispatcher, processor,
in-memory store, multi-processor, and no-op policy. AspNetCore adds six cleanup,
hosted-processing, options, validation, and registration types.

Every one of the 43 neutral/core/AspNetCore C# files is byte-equivalent after the
mechanical `Incursa.Email` to `Incursa.Platform.Email` namespace normalization to
the platform source used by the published core packages. There is no standalone-only
contract or orchestration fix to import.

The provider API inventory includes:

- Postmark sender, probe, validator, message lookup/search client and models,
  options, registration extensions, and webhook provider; webhook parsing,
  authentication, classification, and handler implementation details remain
  internal.
- PostgreSQL outbox store and delivery sink with their options; registration
  extension types remain internal.
- SQL Server outbox store and options; its registration extension type remains
  internal.

All provider C# implementation files, README content, and all 15 substantive test
files at platform provenance commit `0e040ab3` match current public content after
trailing-blank-line normalization. The only source-archive differences are the
three provider project files and the test project file, reflecting repository and
dependency ownership. No implementation or test port was required.

## Dependency boundary

The standalone split made `Incursa.Email.Core` depend on its new abstractions plus
Platform, ExactlyOnce, Idempotency, and Observability; AspNetCore depended on the
standalone neutral packages plus Platform services. That split is intentionally not
adopted. Public provider packages consume `Incursa.Platform.Email` and, where
needed, Platform webhooks, observability, correlation, Dapper, Npgsql, or
SqlClient. This preserves the one-way dependency from public providers to platform.

## Package and version continuity

NuGet.org contains the same-ID provider line through `2026.3.9.853`. GitHub Packages
returned no package for the queried IDs. The configured internal feed could not be
read without authentication, so it must be rechecked immediately before any release.
No stable version is changed here.

The safe known next-version floor is `2026.11.16.1`, which is greater than every
version visible on accessible feeds. This is a floor, not release authorization.
ApiCompat reports only the expected assembly-version regression when comparing the
current local `6.0.9` build to `2026.3.9.853`; suppressing CP0003 produces no public
API differences. Decompiled binary comparison finds only assembly/file/informational
version and repository URL metadata changes. Consequently, a future release must
jump above the date-version line rather than publish a nominal `6.0.10` successor.

## Consumer evidence

An exact local workspace search, excluding source repositories and build output,
found no reference to any of the six `Incursa.Email.*` package IDs. Exact GitHub
default-branch searches likewise found no consumer; the only result was a historical
rename note in the standalone repository. This evidence permits retirement status
but does not authorize deleting the source repository.

## Validation record

The reconciliation validation completed as follows:

- targeted single-source restore and warning-as-error build for
  `tests/Incursa.Platform.Email.Tests` completed with zero warnings and errors;
- email tests passed 21/21, and targeted format plus analyzer verification passed;
- local packs for all three provider packages, with no publication;
- ApiCompat against `2026.3.9.853`, including a second run excluding only CP0003;
- decompiled binary comparison against `2026.3.9.853`;
- regression reruns passed for Cloudflare (50/50), WorkOS (72 passed and one
  intentional skip), Electronic Notary (1/1), and private Proof (37/37);
- final working-tree fingerprints for both read-only source repositories and the
  preserved private slice.

The default two-source restore first reproduced repository-wide NU1507 source-mapping
errors because the authenticated internal feed is configured but unavailable. The
successful validation explicitly selected NuGet.org and disabled audit; no source or
feed configuration was changed.

The command results and final fingerprints are recorded in the task handoff; no
source repository, workflow, package feed, or release producer was mutated.
