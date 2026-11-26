# Postmark and email package authority

## Decision

`integrations-public` is authoritative for the concrete provider packages `Incursa.Platform.Email.Postmark`, `Incursa.Platform.Email.Postgres`, and `Incursa.Platform.Email.SqlServer`.

Provider-neutral contracts, orchestration, in-memory behavior, hosted processing, and idempotency cleanup remain owned by `Incursa.Platform.Email` and `Incursa.Platform.Email.AspNetCore` in `platform`.

## Legacy identities

Standalone `Incursa.Email.*` identities are retirement candidates and must not be reintroduced, aliased, or deleted solely from a repository search. Any compatibility action requires consumer evidence, feed inspection, API comparison, and an explicit migration decision.

## Dependency boundary

Provider packages may depend on the provider-neutral email contracts and the platform capabilities they implement. The neutral packages must not depend on Postmark, PostgreSQL, SQL Server, provider credentials, or provider-specific storage behavior.

## Package continuity

Source consolidation does not authorize a version reset. Before publishing, compare visible feeds, package metadata, and public API baselines, then choose a version greater than every applicable published predecessor. Feed availability and a locally successful pack are not publication authorization.

## Verification

Changes require focused provider tests, package inspection, dependency-boundary validation, and public API compatibility review when applicable. Point-in-time commits, worktrees, test counts, feed observations, and deployment state belong in pull requests or release records rather than this decision.
