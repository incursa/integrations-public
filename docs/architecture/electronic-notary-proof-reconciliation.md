# Electronic Notary public package authority

## Decision

`integrations-public` is authoritative for the public Electronic Notary package family. The public repository contains only the reusable protocol, models, client behavior, and hosting boundaries intended for public consumption.

Non-public implementations, customer workflows, credentials, private endpoints, and internal operational details are outside this repository and must not be named or described in public documentation.

## Boundary rules

- Keep vendor protocol and reusable public API behavior in the public package family.
- Keep provider-neutral abstractions in `platform` when more than one implementation justifies the seam.
- Do not import customer-specific policy, private deployment configuration, or non-public source history.
- Preserve package and API compatibility through the repository's versioning and release controls.

## Verification

Public changes require focused tests, package inspection, dependency-boundary checks, and public API compatibility review when applicable. Record one-run commits, branches, test counts, and publication state in the pull request or release record, not this decision.
