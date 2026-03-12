# Repository Split Plan

## Intent

`integrations-public` was created by copying `platform`, then pruning it down to the public provider-specific implementations that should no longer live in the provider-neutral foundation repo.

## Included in this repository

### Source projects

- `src/Incursa.Integrations.*`
- `src/Incursa.Platform.Email.Postmark/`
- `src/Incursa.Platform.Email.Postgres/`
- `src/Incursa.Platform.Email.SqlServer/`
- `src/Incursa.Platform.InMemory/`
- `src/Incursa.Platform.Postgres/`
- `src/Incursa.Platform.SqlServer/`

### Tests and tooling

- provider-focused tests and smoke hosts that validate the moved implementations
- `tools/migrations/`
- retained shared tooling that is still needed by this repo's build

## Left in `platform`

- provider-neutral capability packages
- shared models and abstractions
- provider-neutral orchestration and hosting adapters

## Assumptions

- cross-repository `ProjectReference` links are acceptable as a first local-development bridge
- namespace and package identities stay intact unless a change is required for correctness
- proprietary connectors remain out of scope for this pass
