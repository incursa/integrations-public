# Public Integrations Scope Boundary

`integrations-public` is intentionally scoped to concrete public implementations. Keep this repository focused on code that is specific to an external provider or provider-backed storage engine.

## In scope

- concrete provider API clients and adapters
- provider-specific implementations of `platform` contracts
- public storage and database implementations
- provider-focused tests and smoke hosts
- tooling that is directly required by moved provider implementations

## Out of scope

- provider-neutral abstractions, contracts, and shared models
- generalized orchestration and durable-processing primitives
- hosting adapters that do not require a concrete provider
- repo-agnostic analyzers and helper CLIs
- customer-specific or proprietary integrations

## Placement rules

- if code can be explained without naming a provider, it probably belongs in `platform`
- if code exists to translate to or from a specific vendor or database, it belongs here
- if classification is unclear, prefer keeping the shared abstraction in `platform` and document the dependency in the split notes

## Naming rule

Package identity is more important than repository naming. A moved package may still keep its `Incursa.Platform.*` name when changing it would create unnecessary churn.
