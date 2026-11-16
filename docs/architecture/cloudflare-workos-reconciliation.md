# Cloudflare and WorkOS reconciliation ledger

## Scope and baselines

- Destination: `incursa/integrations-public` at
  `bb636c9b3cecb0d15eb3209d6804180d058dcaea` before reconciliation.
- Cloudflare source: `incursa/integrations-cloudflare` committed `main` at
  `e0734e18584ba3ada2b3a0b4fab8108c19c8958a`.
- WorkOS source: `incursa/integrations-workos` committed `main` at
  `50c83573488a20e4dbd489a3081135f053ca077d`.
- Standalone working-tree changes were inspected only for boundary awareness and were
  not imported. Reconciliation uses committed Git objects exclusively.

Blob comparison identified exact three-way source baselines:

| Family | Standalone baseline | Evidence |
|---|---|---|
| Cloudflare | `5e367d0e9deb6eee86749512d964a86d2785788d` | All later-touched destination files matched the baseline blobs before replay. |
| WorkOS | `f9b3efee56a6bf5f47e795b1acb01430b9aa4cf5` | All eight later-touched destination files matched the baseline blobs before replay. |

## Cloudflare commit disposition

| Standalone commit | Disposition | Destination evidence |
|---|---|---|
| `f655610ab5c7dc2bd87a94670ede32a43a76b153` | Merged | Null/empty R2 listing handling and `CloudflareR2BlobStoreTests`. |
| `93f5ac9537fadbc7f6a9d9cc6c8e332759d51d8e` | Merged | Bounded multipart upload for seekable objects at least 16 MiB. |
| `db8db7bedb52f7b5680c546e81e15b040a451b82` | Merged | One bounded multipart batch at a time. |
| `ad148c3877a973255d51a1ec14f5d169dbcd0bc5` | Merged | Twelve-part bounded concurrency and ordered completion. |
| `f57b6609e21dc06481318f8524a8a3045737f098` | Merged with compatibility supersession | Per-part timing logs and DI logger injection. The destination also retains the pre-existing two-argument constructor and adds a logger-aware overload, avoiding a binary API break. |
| `204b1144a4d36862e3ef0fe2682d7a7661b56439` and `e0734e18584ba3ada2b3a0b4fab8108c19c8958a` | Intentionally excluded | Standalone release-workflow changes; destination release authority and workflows remain unchanged pending cutover review. |

Public-only `Incursa.Integrations.Cloudflare.CustomDomains`,
`Incursa.Integrations.Cloudflare.Dns`, and the non-packable KvProbe are preserved.

## WorkOS commit disposition

| Standalone commit | Disposition | Destination evidence |
|---|---|---|
| `faeac39c5f68757d985317182fd4db7065a7e5fc` | Merged | User Sessions widget uses `widgets:users-table:manage`; configured overrides remain supported. |
| `e7035b46458080ed48740665002e30426f370127` | Merged | Registered `feed:read` and `feed:publish` permissions map and authorize NuGet, APT, and raw protocol operations. |
| `50c83573488a20e4dbd489a3081135f053ca077d` | Merged | API-key creation sends ISO-8601 `expires_at`, omits legacy `ttl_hours`, and surfaces only allowlisted safe failure diagnostics. |
| Standalone template, Workbench, and release-workflow commits after the baseline | Already present, superseded, or intentionally excluded | They do not change the provider package behavior being reconciled and do not replace destination governance. |

Public-only WorkOS Access, Audit, and Webhooks packages and their tests are preserved.

## Package and version evidence

No package ID, assembly name, project reference, package dependency, repository URL,
or stable version is intentionally changed in this reconciliation.

| Family | Public stable line | Other observed immutable versions | Minimum safe next version, if cutover is approved |
|---|---|---|---|
| Cloudflare | NuGet.org through `6.0.9` | GitHub Packages `2026.11.15.255`; private feed `2026.11.15` | `2026.11.15.256` |
| WorkOS | NuGet.org through `6.0.9` | GitHub Packages `2026.11.15.1333`; private feed `0.2.1` (and older `2026.2.25.32`) | `2026.11.15.1334` |

These values are release lower bounds, not version changes made by this slice. They
must be rechecked immediately before publication because the standalone workflows
remain enabled during review.

## Intentional exclusions and follow-up

- No standalone repository workflow, secret, environment, setting, or release is
  changed.
- No stable package is published and no catalog version is bumped.
- No Electronic Notary/Proof or Postmark/email source is reconciled in this slice.
- Producer cutover still requires package-content evidence, consumer canaries, and a
  final feed recheck.

## Validation evidence

Validation was performed without publishing or changing a package version:

| Check | Result |
|---|---|
| Cloudflare targeted build | Passed with zero warnings and zero errors under `-warnaserror`. |
| Cloudflare tests | 50 passed, 0 failed, 0 skipped. |
| WorkOS targeted build | Passed. The wider project reports 23 pre-existing analyzer warnings in unchanged files; changed files do not contribute warnings. |
| WorkOS tests | 64 passed, 0 failed, 1 pre-existing skip. |
| Format/analyzer verification | Targeted verification passed for every changed Cloudflare and WorkOS file. The whole WorkOS test project still reports pre-existing import-order and comparer findings in untouched tests. |
| No-publish package inspection | Four `6.0.9` packages packed locally. Package IDs, assembly identities, TFMs, and existing project/package references remain intact. Microsoft ApiCompat found no breaking public API changes against all four NuGet.org `6.0.9` packages. |
| Package catalog | Existing Cloudflare and WorkOS entries remain present in `eng/package-catalog.json` and `eng/package-versions.json`; no catalog rewrite or version bump was required. |
| Full repository build diagnostic | Blocked by user-wide NuGet sources (`nuget.org` plus an unauthenticated `incursa` source), producing NU1507/NU1900 and unrelated Aspire vulnerability findings. Targeted restore from NuGet.org with audit disabled succeeded. |
| Workbench | Tool restore succeeded. `doctor` reports the repository's pre-existing missing `.workbench/config.json`, expected paths, and GitHub token; strict validation also reports the pre-existing missing doc schema and broken-link backlog. |
| Hygiene | `git diff --check` passed. Changed source, tests, and architecture docs contain no legacy-brand references. |

The four standalone source repositories were fingerprinted before and after this
work. Their statuses, staged/unstaged diffs, and untracked-file lists were unchanged;
`integrations-private` remained clean. No source workflow or repository setting was
changed.

| Read-only repository | Dirty entries | Unstaged binary-diff SHA-256 |
|---|---:|---|
| `integrations-cloudflare` | 9 | `a99980ebce6bf645decd097120b8de3d5c324e03b74e073cea751aee36bf38b2` |
| `integrations-workos` | 11 | `91aab6a58bd0d0a95776a09eb5682e42d6496f2a313f978379eed034a21f18f8` |
| `integrations-electronicnotary` | 20 | `8809c3b1c20bee36c0524946b608aab90b2b18e52f63cfe3cdb5f3047560dd73` |
| `integrations-postmark` | 8 | `1af7f099c3e620502bd883c7dc967546936af7cb9098b63fd5b357874f702131` |

For every source repository, the staged-diff and untracked-list SHA-256 remained the
empty-input hash `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
