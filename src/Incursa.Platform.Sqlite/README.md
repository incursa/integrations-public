# Incursa.Platform.Sqlite

`Incursa.Platform.Sqlite` provides the Incursa Platform contracts over one local
SQLite database file. It is designed for an independent agent or application running
on one host. It is not a shared-file or multi-host coordination provider.

## Install

```powershell
dotnet add package Incursa.Platform.Sqlite
```

## Register

```csharp
using Incursa.Platform;

var path = Path.GetFullPath("data/platform.sqlite");
Directory.CreateDirectory(Path.GetDirectoryName(path)!);

services.AddSqlitePlatform(path, options =>
{
    options.EnableSchemaDeployment = true;
    options.EnableScheduling = true;
    options.EnableInbox = true;
    options.EnableOutbox = true;
    options.EnableLeases = true;
    options.EnableIdempotency = true;
    options.EnableFanout = true;
    options.EnableOperations = true;
    options.EnableAudit = true;
    options.EnableHealthChecks = true;
    options.EnableWorkers = true;
    options.Workers.PollingInterval = TimeSpan.FromSeconds(5);
    options.Workers.BatchSize = 50;
    options.Workers.LeaseDuration = TimeSpan.FromSeconds(30);
    options.Workers.RetryDelay = TimeSpan.FromSeconds(5);
    options.Workers.MaxAttempts = 5;
});
```

The provider registers the enabled provider-neutral Platform adapters together with
the connection factory, bounded busy/locked retry policy, migration runner, integrity
checker, and backup service. Feature-specific settings are available through
`SqlitePlatformOptions.Inbox`, `.Leases`, `.Idempotency`, `.Operations`, and `.Audit`. Keep one
registration for a database file in a process.

Register provider-neutral `IInboxHandler` and `IOutboxHandler` implementations by
topic to enable the built-in bounded dispatch workers. Successful handlers are
acknowledged; transient failures are rescheduled and permanent or exhausted failures
are marked failed. Delivery is durable at-least-once, so handlers and external effects
must be idempotent.

## Storage guarantees

- Every connection enables WAL mode, `synchronous=FULL`, foreign keys, and the configured busy timeout.
- Busy/locked operations use a bounded retry policy; non-transient errors are not retried.
- Scheduler, inbox, outbox, lease, idempotency, fanout, operation, and audit writes use short SQLite transactions and owner/fencing predicates where applicable.
- Migrations run in a serialized transaction and record SHA-256 SQL hashes to detect drift.
- Integrity checks run `PRAGMA integrity_check` and `PRAGMA foreign_key_check`.
- Online backup checkpoints the WAL, validates source and target, and atomically publishes a temporary sibling file.
- Restore validates a backup and publishes only to an inactive destination; the configured live database and SQLite sidecars are protected.
- The `Incursa.Platform.Sqlite` `System.Diagnostics.Metrics` meter emits local counters for connections, retries, migrations, integrity checks, worker passes, operations, and audit writes; applications can export them with OpenTelemetry.

## Boundary

Place the database on local storage. Fully qualified local paths are required; relative,
URI, drive-relative, UNC, and network paths are rejected. Several processes on one
host can share the local file within SQLite's serialized-writer model. Do not mount the
file on SMB/NFS/CIFS or use it for independent machines. Use
`Incursa.Platform.Postgres` or `Incursa.Platform.SqlServer` for multi-host leases,
schedulers, inbox/outbox dispatch, discovery, control-plane coordination, or central
metrics rollups.

Exactly-once coordination primitives do not make external HTTP calls, emails,
payments, or other effects exactly once. Design handlers to tolerate redelivery and
use an idempotency key at the receiving boundary.

## Documentation

See [the SQLite provider guide](../../docs/sqlite-platform-provider.md) for the
capability matrix, options, migration and drift rules, backup/restore cutover,
concurrency guidance, recovery procedures, and .NET Worker Service and ASP.NET Core
examples.
