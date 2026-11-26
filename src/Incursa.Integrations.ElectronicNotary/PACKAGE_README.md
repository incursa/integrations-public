# Incursa.Integrations.ElectronicNotary

`Incursa.Integrations.ElectronicNotary` is the root layer 1 package family marker for the electronic-notary integration surface in this monorepo.

It exists to give the vendor family a stable public landing point and a coherent naming anchor alongside the other `Incursa.Integrations.*` packages.

## What It Owns

- the vendor-family root package for Electronic Notary integrations
- shared naming, packaging, and discovery anchor points for related public packages

## What It Does Not Own

- the proof client implementation
- ASP.NET Core webhook hosting or healing workflows
- provider-neutral signing or document-workflow capabilities

## Family Map

- `Incursa.Integrations.ElectronicNotary.Abstractions`

Provider-specific non-public implementations are outside this repository and are not part of the public package contract.

## Install

```bash
dotnet add package Incursa.Integrations.ElectronicNotary
```
