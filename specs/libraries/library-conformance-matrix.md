# Library Conformance Matrix

## Scope
Traceability matrix for cross-library interface quality scenarios in:
- `specs/libraries/library-interface-quality-spec.md`

Status values:
- `Covered`: scenario is mapped to one or more automated tests or automation artifacts.
- `Missing`: no mapped test exists yet.
- `Deferred`: intentionally deferred with rationale and backlog tracking.

| Scenario ID | Library | Area | Status | Mapped Test(s) / Artifact(s) |
| --- | --- | --- | --- | --- |
| LIB-GOV-SPEC-001 | All | Governance | Covered | `specs/libraries/library-interface-quality-spec.md`, `specs/libraries/library-conformance-matrix.md` |
| LIB-GOV-TEST-001 | All | Governance | Covered | `scripts/quality/validate-library-traceability.ps1` |
| LIB-GOV-COV-001 | All | Governance | Covered | `scripts/quality/run-library-coverage.ps1` |
| LIB-GOV-MUT-001 | All | Governance | Covered | `scripts/quality/run-library-mutation.ps1` |
| LIB-GOV-FUZZ-001 | All | Governance | Covered | `specs/libraries/library-conformance-matrix.md` |
| LIB-INMEMORY-API-001 | InMemory | PublicApi | Covered | `tests/Incursa.Platform.InMemory.Tests/InMemoryPublicApiContractTests.cs` |
| LIB-INMEMORY-TEST-001 | InMemory | Behavior | Covered | `tests/Incursa.Platform.InMemory.Tests/Incursa.Platform.InMemory.Tests.csproj` |
| LIB-INMEMORY-FUZZ-001 | InMemory | Fuzz | Covered | `tests/Incursa.Platform.InMemory.Tests/InMemoryOutboxFuzzTests.cs` |
| LIB-INMEMORY-MUT-001 | InMemory | Mutation | Covered | `scripts/quality/stryker/inmemory.stryker-config.json` |
| LIB-SQLSERVER-API-001 | SqlServer | PublicApi | Covered | `tests/Incursa.Platform.SqlServer.Tests/SqlPlatformRegistrationTests.cs` |
| LIB-SQLSERVER-TEST-001 | SqlServer | Behavior | Covered | `tests/Incursa.Platform.SqlServer.Tests/Incursa.Platform.SqlServer.Tests.csproj` |
| LIB-SQLSERVER-FUZZ-001 | SqlServer | Fuzz | Covered | `tests/Incursa.Platform.SqlServer.Tests/SqlServerOutboxFuzzTests.cs` |
| LIB-SQLSERVER-MUT-001 | SqlServer | Mutation | Covered | `scripts/quality/stryker/sqlserver.stryker-config.json` |
| LIB-POSTGRES-API-001 | Postgres | PublicApi | Covered | `tests/Incursa.Platform.Postgres.Tests/PostgresPublicApiContractTests.cs` |
| LIB-POSTGRES-TEST-001 | Postgres | Behavior | Covered | `tests/Incursa.Platform.Postgres.Tests/Incursa.Platform.Postgres.Tests.csproj` |
| LIB-POSTGRES-FUZZ-001 | Postgres | Fuzz | Covered | `tests/Incursa.Platform.Postgres.Tests/PostgresOutboxFuzzTests.cs` |
| LIB-POSTGRES-MUT-001 | Postgres | Mutation | Covered | `scripts/quality/stryker/postgres.stryker-config.json` |
| LIB-CORE-API-001 | Core | PublicApi | Missing | Core tests are not present in this repository |
| LIB-CORE-TEST-001 | Core | Behavior | Missing | Core tests are not present in this repository |
| LIB-AUDIT-API-001 | Audit | PublicApi | Missing | Audit tests are not present in this repository |
| LIB-AUDIT-TEST-001 | Audit | Behavior | Missing | Audit tests are not present in this repository |
| LIB-CORRELATION-API-001 | Correlation | PublicApi | Missing | Correlation tests are not present in this repository |
| LIB-CORRELATION-TEST-001 | Correlation | Behavior | Missing | Correlation tests are not present in this repository |
| LIB-EMAIL-API-001 | Email | PublicApi | Covered | `tests/Incursa.Platform.Email.Tests/EmailMessageValidatorTests.cs` |
| LIB-EMAIL-TEST-001 | Email | Behavior | Covered | `tests/Incursa.Platform.Email.Tests/Incursa.Platform.Email.Tests.csproj` |
| LIB-EMAILASPNETCORE-API-001 | Email.AspNetCore | PublicApi | Covered | `tests/Incursa.Platform.Email.Tests/EmailAspNetCoreExtensionsTests.cs` |
| LIB-EMAILASPNETCORE-TEST-001 | Email.AspNetCore | Behavior | Covered | `tests/Incursa.Platform.Email.Tests/Incursa.Platform.Email.Tests.csproj` |
| LIB-EXACTLYONCE-API-001 | ExactlyOnce | PublicApi | Missing | ExactlyOnce tests are not present in this repository |
| LIB-EXACTLYONCE-TEST-001 | ExactlyOnce | Behavior | Missing | ExactlyOnce tests are not present in this repository |
| LIB-HEALTHPROBE-API-001 | HealthProbe | PublicApi | Missing | HealthProbe tests are not present in this repository |
| LIB-HEALTHPROBE-TEST-001 | HealthProbe | Behavior | Missing | HealthProbe tests are not present in this repository |
| LIB-IDEMPOTENCY-API-001 | Idempotency | PublicApi | Missing | No direct Idempotency library test project yet |
| LIB-IDEMPOTENCY-TEST-001 | Idempotency | Behavior | Missing | No direct Idempotency behavior test suite yet |
| LIB-METRICSASPNETCORE-API-001 | Metrics.AspNetCore | PublicApi | Missing | No direct Metrics.AspNetCore tests yet |
| LIB-METRICSASPNETCORE-TEST-001 | Metrics.AspNetCore | Behavior | Missing | No direct Metrics.AspNetCore behavior suite yet |
| LIB-METRICSHTTPSERVER-API-001 | Metrics.HttpServer | PublicApi | Missing | No direct Metrics.HttpServer tests yet |
| LIB-METRICSHTTPSERVER-TEST-001 | Metrics.HttpServer | Behavior | Missing | No direct Metrics.HttpServer behavior suite yet |
| LIB-MODULARITY-API-001 | Modularity | PublicApi | Missing | Modularity tests are not present in this repository |
| LIB-MODULARITY-TEST-001 | Modularity | Behavior | Missing | Modularity tests are not present in this repository |
| LIB-MODULARITYASPNETCORE-API-001 | Modularity.AspNetCore | PublicApi | Missing | Modularity.AspNetCore tests are not present in this repository |
| LIB-MODULARITYASPNETCORE-TEST-001 | Modularity.AspNetCore | Behavior | Missing | Modularity.AspNetCore tests are not present in this repository |
| LIB-MODULARITYRAZOR-API-001 | Modularity.Razor | PublicApi | Missing | Modularity.Razor tests are not present in this repository |
| LIB-MODULARITYRAZOR-TEST-001 | Modularity.Razor | Behavior | Missing | Modularity.Razor tests are not present in this repository |
| LIB-OBSERVABILITY-API-001 | Observability | PublicApi | Missing | Observability tests are not present in this repository |
| LIB-OBSERVABILITY-TEST-001 | Observability | Behavior | Missing | Observability tests are not present in this repository |
| LIB-OPERATIONS-API-001 | Operations | PublicApi | Missing | Operations tests are not present in this repository |
| LIB-OPERATIONS-TEST-001 | Operations | Behavior | Missing | Operations tests are not present in this repository |
| LIB-WEBHOOKS-API-001 | Webhooks | PublicApi | Missing | Webhooks tests are not present in this repository |
| LIB-WEBHOOKS-TEST-001 | Webhooks | Behavior | Missing | Webhooks tests are not present in this repository |
| LIB-WEBHOOKSASPNETCORE-API-001 | Webhooks.AspNetCore | PublicApi | Missing | Webhooks.AspNetCore tests are not present in this repository |
| LIB-WEBHOOKSASPNETCORE-TEST-001 | Webhooks.AspNetCore | Behavior | Missing | Webhooks.AspNetCore tests are not present in this repository |

## Next Ratchet Steps
- Stand up dedicated tests for Idempotency and Metrics libraries.
- Add non-provider mutation configs for Operations and Webhooks once baseline runtime stabilizes.
