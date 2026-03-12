# Incursa.Integrations.ElectronicNotary

This folder is the landing page for the Electronic Notary layer 1 integration family.

Use it as the starting point when you are browsing the public Electronic Notary package family.

## Family Map

- `Incursa.Integrations.ElectronicNotary`: root package and family anchor
- `Incursa.Integrations.ElectronicNotary.Abstractions`: shared contracts for the family

The provider-specific Proof implementation moved to the private integrations repository and now ships as:

- `Incursa.Integrations.Proof`
- `Incursa.Integrations.Proof.AspNetCore`

## Relationship To Layer 2

This family is currently intentionally vendor-shaped. It does not define a provider-neutral notarization or signature capability.

## See Also

- `PACKAGE_README.md` for the NuGet package overview
