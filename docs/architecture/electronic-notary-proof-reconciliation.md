# Electronic Notary and Proof reconciliation ledger

## Scope and baselines

- Public destination: `incursa/integrations-public` at
  `bb636c9b3cecb0d15eb3209d6804180d058dcaea`, preserving the uncommitted
  Cloudflare/WorkOS reconciliation already in progress.
- Private destination: `incursa/integrations-private` at
  `d5f97cd018a2ff80ffa5872f3d81fa849ba80c7c`.
- Read-only source: `incursa/integrations-electronicnotary` committed `main` at
  `dce3a1704436289851995f386e42e982b36b03c7`.
- Source implementation baseline: `229df899ecb434925df3f0d9a10ac52bc496139f`.
- Source hardening baseline: `11e038248d727713703682b19ae241cfe2cb9cce`.
- Private Proof import: `1c2a53beba0d5e1a3166c320a4633c18984293c0`.

The source working tree contains a 20-file branding cleanup. It was inspected only
to distinguish branding from behavior and was not imported. All reconciliation used
committed Git objects.

## Authority decision

- `Incursa.Integrations.ElectronicNotary` and
  `Incursa.Integrations.ElectronicNotary.Abstractions` remain public and authoritative
  in `integrations-public`.
- `Incursa.Integrations.Proof` and `Incursa.Integrations.Proof.AspNetCore` remain
  private and authoritative in `integrations-private`.
- The published legacy IDs `Incursa.Integrations.ElectronicNotary.Proof` and
  `Incursa.Integrations.ElectronicNotary.Proof.AspNetCore` are frozen. This slice does
  not create aliases, type forwarders, or new packages for those IDs.

## Commit disposition

| Standalone commit or range | Disposition | Evidence |
|---|---|---|
| `229df899ecb434925df3f0d9a10ac52bc496139f` | Already present, split by authority | Generic marker/abstraction content is in public; Proof implementation and tests were renamed into private. |
| `77446247cd4dcfbf3d1604383d8ffcede05ca7a3` | Superseded | Template, dependency-manifest, and test-project changes are replaced by destination repository conventions. |
| `23a012012c0934edc976cbabdc42f353c07db957`, `6d110190b81835ad3bb6a6d7a6850700c3bab81f` | Superseded | Root package icon/readme metadata is governed by the public package baseline. |
| `a922a444aa2407afc02321b46e8cf6dc30eecc49` | Merged | The ported ASP.NET Core webhook regression test removes hosted services before exercising deterministic webhook processing. |
| `11e038248d727713703682b19ae241cfe2cb9cce` | Already present with one merged test gap | Retry/circuit behavior, telemetry, persistence locking/quarantine, webhook authentication/dispatch hardening, migrations, and unit tests already match the private implementation. The missing end-to-end webhook test was ported. |
| Release/template commits from `4b179d97a20ea17dd71005f3f3b4914ec1607ee4` through `df1751e058214463ba1ba440ae003ea61bf0b538` | Intentionally excluded | Standalone workflow and template mechanics do not replace destination release governance. |
| `1054d2f53643229fcf78b99b13a49c662d13eaee`, `25baa704dac4537b82e59e2fa93ad7c2b288b6a4`, `22b9ad4f197eaf0906b910af409e014931676db9`, `845b85ea6ddf1cc7d0a7af97bcf10bc5907dd9b6` | Intentionally excluded | Standalone feed and workflow changes are outside source reconciliation. |
| `0118be607024db8a93f5d54079ef456ec3502af5`, `ea7d1089adcc7b01013a2ea01afe0d613bbc2d36`, `1d5245a9cae40509a533a4a368158d9f4e5d8846`, `bb36a4cdaa60f14e84febb7197c5e7325f2b5ad9`, `dce3a1704436289851995f386e42e982b36b03c7` | Already present, unrelated, or excluded | Scope docs, unrelated MimeKit/mutation tooling, and worker bootstrap do not change Electronic Notary or Proof runtime behavior. |

## Exhaustive path disposition

The following groups cover every one of the 85 committed source/project/test paths
under the standalone integration roots at `dce3a170...`.

| Source path group | Count | Disposition |
|---|---:|---|
| `src/Incursa.Integrations.ElectronicNotary/**` | 3 | `ElectronicNotaryRoot.cs` is blob-identical in public. The project and package readme are superseded by the public `6.0.9` packaging baseline. |
| `src/Incursa.Integrations.ElectronicNotary.Abstractions/**` | 1 | The project exists in public; its project metadata is superseded by the public `6.0.9` baseline. |
| `src/Incursa.Integrations.ElectronicNotary.Proof/**` | 38 | Every implementation, contract, generated definition, and assembly-friend surface is already present under `src/Incursa.Integrations.Proof/**` after the intentional namespace/assembly rename. The project file is superseded by private identity, dependency, and package metadata. |
| `src/Incursa.Integrations.ElectronicNotary.Proof.AspNetCore/**` | 30 | Every webhook, healing, persistence, migration, and hosting behavior is already present under `src/Incursa.Integrations.Proof.AspNetCore/**`. Differences are namespace/branding, using order, async style, and private package metadata. |
| `tests/Incursa.Integrations.ElectronicNotary.Tests/Incursa.Integrations.ElectronicNotary.Tests.csproj` | 1 | Superseded by the private xUnit test project and the public generic sanity-test project. |
| Ten Proof unit-test files other than the ASP.NET Core integration test | 10 | Already present in `tests/Incursa.Integrations.Proof.Tests/**`, adapted from MSTest to xUnit without behavior loss. |
| `tests/Incursa.Integrations.ElectronicNotary.Tests/ProofWebhookAspNetCoreIntegrationTests.cs` | 1 | Merged into the private test project with canonical namespaces and an `Incursa.Platform.InMemory` test-only dependency. |
| `tests/Incursa.Integrations.ElectronicNotary.Tests/SanityTests.cs` | 1 | Blob-identical generic-root coverage remains in the public Electronic Notary test project. |

## Behavior, API, dependency, and package comparison

After normalizing only the intentional package/namespace rename, legacy platform
branding, xUnit conversion, and repository style, all 67 standalone Proof production
files are behavior-equivalent to the private implementations. No production code was
replaced.

| Surface | Legacy published package | Canonical destination package |
|---|---|---|
| Core ID/assembly | `Incursa.Integrations.ElectronicNotary.Proof` `6.0.3` | `Incursa.Integrations.Proof` `6.0.2` |
| ASP.NET ID/assembly | `Incursa.Integrations.ElectronicNotary.Proof.AspNetCore` `6.0.3` | `Incursa.Integrations.Proof.AspNetCore` `6.0.2` |
| Repository metadata | `incursa/platform` at `2ce57a3...` | `incursa/integrations-private` at `d5f97cd...` |
| Generic abstraction dependency | Required by both legacy packages | Not required by canonical private packages |
| Webhook dependencies | `Incursa.Platform.Webhooks*` `6.0.3` | `Incursa.Platform.Webhooks*` `6.0.5` |
| Packed runtime assets | One `net10.0` assembly per package | One `net10.0` assembly per package, plus private readme/icon metadata |

The differing IDs, assembly names, namespaces, and dependency graphs are a material
identity incompatibility. Microsoft ApiCompat cannot treat them as the same assembly.
Supporting both families requires an explicit compatibility-package lifetime and
consumer migration policy, so that part is blocked by decision rather than guessed.

GitHub organization code search found no actual consumer of the legacy package IDs;
the only hits were catalog classification scripts. The only external canonical-ID
hits were internal-feed source mappings in `incursa/govern`, not package references.

## Versions and publication floor

| Package family | Highest observed canonical version | Monotonic next floor |
|---|---:|---:|
| Public generic Electronic Notary | NuGet.org `6.0.9` | `6.0.10` |
| Frozen legacy Electronic Notary Proof | NuGet.org `6.0.3` | `6.0.4` only if a separate decision revives the IDs |
| Private canonical Proof | Destination manifest/package `6.0.2` | `6.0.3`, subject to an authenticated internal-feed recheck |

The GitHub Packages API returned no canonical private package and required additional
scope for legacy package history. Historical private workflow logs had expired. These
are lower bounds only; publication still requires an authenticated feed query. No
version was changed and nothing was published.

## Validation evidence

- Public generic `6.0.9` packages packed locally without publication. Microsoft
  ApiCompat found no breaking changes versus both NuGet.org `6.0.9` baselines.
- Private canonical `6.0.2` packages packed locally without publication; IDs,
  assemblies, SourceLink repository metadata, dependencies, and contents were
  inspected.
- The private Proof suite passes 37 tests, including the two newly ported signed and
  invalid-signature end-to-end cases.
- The affected private test project builds under `-warnaserror` with zero warnings
  and zero errors. Targeted formatter and analyzer verification passes for the new
  integration test.
- Public generic Electronic Notary tests pass 1/1. Prior Cloudflare tests pass 50/50;
  prior WorkOS tests pass 64 with one existing skip.
- Full public and private repository diagnostics remain blocked by the user-wide
  mixed NuGet sources (`nuget.org` and unauthenticated `incursa`), NU1507/NU1900,
  and existing dependency vulnerability findings. Private diagnostics specifically
  report `Microsoft.Build.Tasks.Git` `10.0.201` and the Proof dependency graph's
  transitive `System.Security.Cryptography.Pkcs` `9.0.11`; this slice did not create
  either dependency. Targeted restores from NuGet.org with audit disabled succeed.
- `git diff --check`, untracked-file whitespace checks, and changed-file legacy-brand
  scans pass in both destination repositories.

## Read-only boundary evidence

The standalone source fingerprint was identical before and after reconciliation:

| Property | Value |
|---|---|
| HEAD | `dce3a1704436289851995f386e42e982b36b03c7` |
| Dirty entries | 20 |
| Unstaged binary-diff SHA-256 | `8809c3b1c20bee36c0524946b608aab90b2b18e52f63cfe3cdb5f3047560dd73` |
| Staged-diff SHA-256 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |
| Untracked-list SHA-256 | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` |

The previously recorded Cloudflare, WorkOS, and Postmark dirty-tree counts and binary
diff hashes also remained identical. No workflow, setting, secret, environment,
publication, or unrelated repository was changed.
